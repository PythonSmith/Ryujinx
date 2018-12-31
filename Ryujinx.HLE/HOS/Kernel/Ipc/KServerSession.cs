using Ryujinx.Common;
using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Kernel.Ipc
{
    class KServerSession : KSynchronizationObject
    {
        private static readonly MemoryState[] IpcMemoryStates = new MemoryState[]
        {
            MemoryState.IpcBuffer3,
            MemoryState.IpcBuffer0,
            MemoryState.IpcBuffer1,
            (MemoryState)0xfffce5d4 //This is invalid, shouldn't be accessed.
        };

        private struct Message
        {
            public ulong Address     { get; }
            public ulong DramAddress { get; }
            public ulong Size        { get; }
            public bool  IsCustom    { get; }

            public Message(KThread thread, ulong customCmdBuffAddress, ulong customCmdBuffSize)
            {
                IsCustom = customCmdBuffAddress != 0;

                if (IsCustom)
                {
                    Address = customCmdBuffAddress;
                    Size    = customCmdBuffSize;

                    KProcess process = thread.Owner;

                    DramAddress = process.MemoryManager.GetDramAddressFromVa(Address);
                }
                else
                {
                    Address     = thread.TlsAddress;
                    DramAddress = thread.TlsDramAddress;
                    Size        = 0x100;
                }
            }
        }

        private struct MessageHeader
        {
            public uint Word0 { get; }
            public uint Word1 { get; }
            public uint Word2 { get; }

            public uint PointerBuffersCount  { get; }
            public uint SendBuffersCount     { get; }
            public uint ReceiveBuffersCount  { get; }
            public uint ExchangeBuffersCount { get; }

            public uint RawDataSizeInWords { get; }

            public uint ReceiveListType { get; }

            public uint MessageSizeInWords       { get; }
            public uint ReceiveListOffsetInWords { get; }
            public uint ReceiveListOffset        { get; }

            public bool HasHandles { get; }

            public bool HasPid { get; }

            public uint CopyHandlesCount { get; }
            public uint MoveHandlesCount { get; }

            public MessageHeader(uint word0, uint word1, uint word2)
            {
                Word0 = word0;
                Word1 = word1;
                Word2 = word2;

                HasHandles = word1 >> 31 != 0;

                uint handleDescSizeInWords = 0;

                if (HasHandles)
                {
                    uint pidSize = (word2 & 1) * 8;

                    HasPid = pidSize != 0;

                    CopyHandlesCount = (word2 >> 1) & 0xf;
                    MoveHandlesCount = (word2 >> 5) & 0xf;

                    handleDescSizeInWords = (pidSize + CopyHandlesCount * 4 + MoveHandlesCount * 4) / 4;
                }
                else
                {
                    HasPid = false;

                    CopyHandlesCount = 0;
                    MoveHandlesCount = 0;
                }

                PointerBuffersCount  = (word0 >> 16) & 0xf;
                SendBuffersCount     = (word0 >> 20) & 0xf;
                ReceiveBuffersCount  = (word0 >> 24) & 0xf;
                ExchangeBuffersCount =  word0 >> 28;

                uint pointerDescSizeInWords  = PointerBuffersCount  * 2;
                uint sendDescSizeInWords     = SendBuffersCount     * 3;
                uint receiveDescSizeInWords  = ReceiveBuffersCount  * 3;
                uint exchangeDescSizeInWords = ExchangeBuffersCount * 3;

                RawDataSizeInWords = word1 & 0x3ff;

                ReceiveListType = (word1 >> 10) & 0xf;

                ReceiveListOffsetInWords = (word1 >> 20) & 0x7ff;

                uint paddingSizeInWords = HasHandles ? 3u : 2u;

                MessageSizeInWords = pointerDescSizeInWords  +
                                     sendDescSizeInWords     +
                                     receiveDescSizeInWords  +
                                     exchangeDescSizeInWords +
                                     RawDataSizeInWords      +
                                     paddingSizeInWords      +
                                     handleDescSizeInWords;

                if (ReceiveListOffsetInWords == 0)
                {
                    ReceiveListOffsetInWords = MessageSizeInWords;
                }

                ReceiveListOffset = ReceiveListOffsetInWords * 4;
            }
        }

        private struct PointerBufferDesc
        {
            public uint ReceiveIndex { get; }

            public uint  BufferSize    { get; }
            public ulong BufferAddress { get; set; }

            public PointerBufferDesc(ulong dword)
            {
                ReceiveIndex = (uint)dword & 0xf;
                BufferSize   = (uint)dword >> 16;

                BufferAddress  = (dword >> 2)  & 0x70;
                BufferAddress |= (dword >> 12) & 0xf;

                BufferAddress = (BufferAddress << 32) | (dword >> 32);
            }

            public ulong Pack()
            {
                ulong dword = (ReceiveIndex & 0xf) | ((BufferSize & 0xffff) << 16);

                dword |=  BufferAddress << 32;
                dword |= (BufferAddress >> 20) & 0xf000;
                dword |= (BufferAddress >> 30) & 0xffc0;

                return dword;
            }
        }

        private KSession _parent;

        private LinkedList<KSessionRequest> _requests;

        private KSessionRequest _activeRequest;

        public KServerSession(Horizon system, KSession parent) : base(system)
        {
            _parent = parent;

            _requests = new LinkedList<KSessionRequest>();
        }

        public KernelResult EnqueueRequest(KSessionRequest request)
        {
            if (_parent.ClientSession.State != ChannelState.Open)
            {
                return KernelResult.PortRemoteClosed;
            }

            if (request.AsyncEvent == null)
            {
                if (request.ClientThread.ShallBeTerminated ||
                    request.ClientThread.SchedFlags == ThreadSchedState.TerminationPending)
                {
                    return KernelResult.ThreadTerminating;
                }

                request.ClientThread.Reschedule(ThreadSchedState.Paused);
            }

            _requests.AddLast(request);

            if (_requests.Count == 1)
            {
                Signal();
            }

            return KernelResult.Success;
        }

        public KernelResult Receive(ulong customCmdBuffAddr = 0, ulong customCmdBuffSize = 0)
        {
            KThread  serverThread  = System.Scheduler.GetCurrentThread();
            KProcess serverProcess = serverThread.Owner;

            System.CriticalSection.Enter();

            if (_parent.ClientSession.State != ChannelState.Open)
            {
                System.CriticalSection.Leave();

                return KernelResult.PortRemoteClosed;
            }

            if (_activeRequest != null || !PickRequest(out KSessionRequest request))
            {
                System.CriticalSection.Leave();

                return KernelResult.NotFound;
            }

            if (request.ClientThread == null)
            {
                System.CriticalSection.Leave();

                return KernelResult.PortRemoteClosed;
            }

            KThread  clientThread  = request.ClientThread;
            KProcess clientProcess = clientThread.Owner;

            System.CriticalSection.Leave();

            _activeRequest = request;

            request.ServerProcess = serverProcess;

            Message clientMsg = new Message(
                clientThread,
                request.CustomCmdBuffAddr,
                request.CustomCmdBuffSize);

            Message serverMsg = new Message(serverThread, customCmdBuffAddr, customCmdBuffSize);

            MessageHeader header = GetClientMessageHeader(clientMsg);

            KernelResult serverResult = KernelResult.NotFound;
            KernelResult clientResult = KernelResult.Success;

            void CleanUpForError()
            {
                if (request.BufferDescriptorTable.UnmapServerBuffers(serverProcess.MemoryManager) == KernelResult.Success)
                {
                    request.BufferDescriptorTable.RestoreClientBuffers(clientProcess.MemoryManager);
                }

                CloseAllHandles(serverMsg, header, serverProcess);

                System.CriticalSection.Enter();

                _activeRequest = null;

                if (_requests.Count != 0)
                {
                    Signal();
                }

                System.CriticalSection.Leave();

                WakeClient(request, clientResult);
            }

            if (header.ReceiveListType < 2 &&
                header.ReceiveListOffset > clientMsg.Size)
            {
                CleanUpForError();

                return KernelResult.InvalidCombination;
            }
            else if (header.ReceiveListType == 2 &&
                     header.ReceiveListOffset + 8 > clientMsg.Size)
            {
                CleanUpForError();

                return KernelResult.InvalidCombination;
            }
            else if (header.ReceiveListType > 2 &&
                     header.ReceiveListType * 8 - 0x10 + header.ReceiveListOffset > clientMsg.Size)
            {
                CleanUpForError();

                return KernelResult.InvalidCombination;
            }

            if (header.ReceiveListOffsetInWords < header.MessageSizeInWords)
            {
                CleanUpForError();

                return KernelResult.InvalidCombination;
            }

            if (header.MessageSizeInWords * 4 > clientMsg.Size)
            {
                CleanUpForError();

                return KernelResult.CmdBufferTooSmall;
            }

            ulong[] receiveList = GetReceiveList(clientMsg, header.ReceiveListType, header.ReceiveListOffset);

            serverProcess.CpuMemory.WriteUInt32((long)serverMsg.Address + 0, header.Word0);
            serverProcess.CpuMemory.WriteUInt32((long)serverMsg.Address + 4, header.Word1);

            uint offset;

            //Copy handles.
            if (header.HasHandles)
            {
                if (header.MoveHandlesCount != 0)
                {
                    CleanUpForError();

                    return KernelResult.InvalidCombination;
                }

                serverProcess.CpuMemory.WriteUInt32((long)serverMsg.Address + 8, header.Word2);

                offset = 3;

                if (header.HasPid)
                {
                    serverProcess.CpuMemory.WriteInt64((long)serverMsg.Address + offset * 4, clientProcess.Pid);

                    offset += 2;
                }

                for (int index = 0; index < header.CopyHandlesCount; index++)
                {
                    int newHandle = 0;

                    int handle = System.Device.Memory.ReadInt32((long)clientMsg.DramAddress + offset * 4);

                    if (clientResult == KernelResult.Success && handle != 0)
                    {
                        clientResult = GetCopyObjectHandle(clientThread, serverProcess, handle, out newHandle);
                    }

                    serverProcess.CpuMemory.WriteInt32((long)serverMsg.Address + offset * 4, newHandle);

                    offset++;
                }

                for (int index = 0; index < header.MoveHandlesCount; index++)
                {
                    int newHandle = 0;

                    int handle = System.Device.Memory.ReadInt32((long)clientMsg.DramAddress + offset * 4);

                    if (handle != 0)
                    {
                        if (clientResult == KernelResult.Success)
                        {
                            clientResult = GetMoveObjectHandle(clientProcess, serverProcess, handle, out newHandle);
                        }
                        else
                        {
                            clientProcess.HandleTable.CloseHandle(handle);
                        }
                    }

                    serverProcess.CpuMemory.WriteInt32((long)serverMsg.Address + offset * 4, newHandle);

                    offset++;
                }

                if (clientResult != KernelResult.Success)
                {
                    CleanUpForError();

                    return serverResult;
                }
            }
            else
            {
                offset = 2;
            }

            //Copy pointer/receive list buffers.
            for (int index = 0; index < header.PointerBuffersCount; index++)
            {
                ulong pointerDesc = System.Device.Memory.ReadUInt64((long)clientMsg.DramAddress + offset * 4);

                PointerBufferDesc descriptor = new PointerBufferDesc(pointerDesc);

                if (descriptor.BufferSize != 0)
                {
                    clientResult = GetReceiveListAddress(
                        descriptor,
                        serverMsg,
                        header.ReceiveListType,
                        header.MessageSizeInWords,
                        receiveList,
                        out ulong recvListBufferAddress);

                    if (clientResult != KernelResult.Success)
                    {
                        CleanUpForError();

                        return serverResult;
                    }

                    clientResult = clientProcess.MemoryManager.CopyDataToCurrentProcess(
                        recvListBufferAddress,
                        descriptor.BufferSize,
                        descriptor.BufferAddress,
                        MemoryState.IsPoolAllocated,
                        MemoryState.IsPoolAllocated,
                        MemoryPermission.Read,
                        MemoryAttribute.Uncached,
                        MemoryAttribute.None);

                    if (clientResult != KernelResult.Success)
                    {
                        CleanUpForError();

                        return serverResult;
                    }

                    descriptor.BufferAddress = recvListBufferAddress;
                }
                else
                {
                    descriptor.BufferAddress = 0;
                }

                serverProcess.CpuMemory.WriteUInt64((long)serverMsg.Address + offset * 4, descriptor.Pack());

                offset += 2;
            }

            //Copy send, receive and exchange buffers.
            uint totalBuffersCount =
                header.SendBuffersCount    +
                header.ReceiveBuffersCount +
                header.ExchangeBuffersCount;

            for (int index = 0; index < totalBuffersCount; index++)
            {
                long clientDescAddress = (long)clientMsg.DramAddress + offset * 4;

                uint descWord0 = System.Device.Memory.ReadUInt32(clientDescAddress + 0);
                uint descWord1 = System.Device.Memory.ReadUInt32(clientDescAddress + 4);
                uint descWord2 = System.Device.Memory.ReadUInt32(clientDescAddress + 8);

                bool isSendDesc     = index <  header.SendBuffersCount;
                bool isExchangeDesc = index >= header.SendBuffersCount + header.ReceiveBuffersCount;

                bool notReceiveDesc = isSendDesc || isExchangeDesc;
                bool isReceiveDesc  = !notReceiveDesc;

                MemoryPermission permission = index >= header.SendBuffersCount
                    ? MemoryPermission.ReadAndWrite
                    : MemoryPermission.Read;

                uint sizeHigh4 = (descWord2 >> 24) & 0xf;

                ulong bufferSize = descWord0 | (ulong)sizeHigh4 << 32;

                ulong dstAddress = 0;

                if (bufferSize != 0)
                {
                    ulong bufferAddress;

                    bufferAddress  =   descWord2 >> 28;
                    bufferAddress |= ((descWord2 >> 2) & 7) << 4;

                    bufferAddress = (bufferAddress << 32) | descWord1;

                    MemoryState state = IpcMemoryStates[(descWord2 + 1) & 3];

                    clientResult = serverProcess.MemoryManager.MapBufferFromClientProcess(
                        bufferSize,
                        bufferAddress,
                        clientProcess.MemoryManager,
                        permission,
                        state,
                        notReceiveDesc,
                        out dstAddress);

                    if (clientResult != KernelResult.Success)
                    {
                        CleanUpForError();

                        return serverResult;
                    }

                    if (isSendDesc)
                    {
                        clientResult = request.BufferDescriptorTable.AddSendBuffer(bufferAddress, dstAddress, bufferSize, state);
                    }
                    else if (isReceiveDesc)
                    {
                        clientResult = request.BufferDescriptorTable.AddReceiveBuffer(bufferAddress, dstAddress, bufferSize, state);
                    }
                    else /* if (isExchangeDesc) */
                    {
                        clientResult = request.BufferDescriptorTable.AddExchangeBuffer(bufferAddress, dstAddress, bufferSize, state);
                    }

                    if (clientResult != KernelResult.Success)
                    {
                        CleanUpForError();

                        return serverResult;
                    }
                }

                descWord1 = (uint)dstAddress;

                descWord2 &= 3;

                descWord2 |= sizeHigh4 << 24;

                descWord2 |= (uint)(dstAddress >> 34) & 0x3ffffffc;
                descWord2 |= (uint)(dstAddress >> 4)  & 0xf0000000;

                long serverDescAddress = (long)serverMsg.Address + offset * 4;

                serverProcess.CpuMemory.WriteUInt32(serverDescAddress + 0, descWord0);
                serverProcess.CpuMemory.WriteUInt32(serverDescAddress + 4, descWord1);
                serverProcess.CpuMemory.WriteUInt32(serverDescAddress + 8, descWord2);

                offset += 3;
            }

            //Copy raw data.
            if (header.RawDataSizeInWords != 0)
            {
                ulong copySrc = clientMsg.Address + offset * 4;
                ulong copyDst = serverMsg.Address + offset * 4;

                ulong copySize = header.RawDataSizeInWords * 4;

                if (serverMsg.IsCustom || clientMsg.IsCustom)
                {
                    MemoryPermission permission = clientMsg.IsCustom
                        ? MemoryPermission.None
                        : MemoryPermission.Read;

                    clientResult = clientProcess.MemoryManager.CopyDataToCurrentProcess(
                        copyDst,
                        copySize,
                        copySrc,
                        MemoryState.IsPoolAllocated,
                        MemoryState.IsPoolAllocated,
                        permission,
                        MemoryAttribute.Uncached,
                        MemoryAttribute.None);
                }
                else
                {
                    copySrc = clientProcess.MemoryManager.GetDramAddressFromVa(copySrc);
                    copyDst = serverProcess.MemoryManager.GetDramAddressFromVa(copyDst);

                    System.Device.Memory.Copy(copyDst, copySrc, copySize);
                }

                if (clientResult != KernelResult.Success)
                {
                    CleanUpForError();

                    return serverResult;
                }
            }

            return KernelResult.Success;
        }

        public KernelResult Reply(ulong customCmdBuffAddr = 0, ulong customCmdBuffSize = 0)
        {
            KThread  serverThread  = System.Scheduler.GetCurrentThread();
            KProcess serverProcess = serverThread.Owner;

            System.CriticalSection.Enter();

            if (_activeRequest == null)
            {
                System.CriticalSection.Leave();

                return KernelResult.InvalidState;
            }

            KSessionRequest request = _activeRequest;

            _activeRequest = null;

            if (_requests.Count != 0)
            {
                Signal();
            }

            System.CriticalSection.Leave();

            KThread  clientThread  = request.ClientThread;
            KProcess clientProcess = clientThread.Owner;

            Message clientMsg = new Message(
                clientThread,
                request.CustomCmdBuffAddr,
                request.CustomCmdBuffSize);

            Message serverMsg = new Message(serverThread, customCmdBuffAddr, customCmdBuffSize);

            uint word0 = serverProcess.CpuMemory.ReadUInt32((long)serverMsg.Address + 0);
            uint word1 = serverProcess.CpuMemory.ReadUInt32((long)serverMsg.Address + 4);
            uint word2 = serverProcess.CpuMemory.ReadUInt32((long)serverMsg.Address + 8);

            MessageHeader header = new MessageHeader(word0, word1, word2);

            MessageHeader clientHeader = GetClientMessageHeader(clientMsg);

            KernelResult clientResult = KernelResult.Success;
            KernelResult serverResult = KernelResult.Success;

            void CleanUpForError()
            {
                CloseAllHandles(clientMsg, header, clientProcess);

                CancelRequest(request, clientResult);
            }

            if (clientHeader.ReceiveListType < 2 &&
                clientHeader.ReceiveListOffset > clientMsg.Size)
            {
                CleanUpForError();

                return KernelResult.InvalidCombination;
            }
            else if (clientHeader.ReceiveListType == 2 &&
                     clientHeader.ReceiveListOffset + 8 > clientMsg.Size)
            {
                CleanUpForError();

                return KernelResult.InvalidCombination;
            }
            else if (clientHeader.ReceiveListType > 2 &&
                     clientHeader.ReceiveListType * 8 - 0x10 + clientHeader.ReceiveListOffset > clientMsg.Size)
            {
                CleanUpForError();

                return KernelResult.InvalidCombination;
            }

            if (clientHeader.ReceiveListOffsetInWords < clientHeader.MessageSizeInWords)
            {
                CleanUpForError();

                return KernelResult.InvalidCombination;
            }

            if (header.MessageSizeInWords * 4 > clientMsg.Size)
            {
                CleanUpForError();

                return KernelResult.CmdBufferTooSmall;
            }

            if (header.SendBuffersCount     != 0 ||
                header.ReceiveBuffersCount  != 0 ||
                header.ExchangeBuffersCount != 0)
            {
                CleanUpForError();

                return KernelResult.InvalidCombination;
            }

            //Read receive list.
            ulong[] receiveList = GetReceiveList(
                clientMsg,
                clientHeader.ReceiveListType,
                clientHeader.ReceiveListOffset);

            //Copy receive and exchange buffers.
            clientResult = request.BufferDescriptorTable.CopyBuffersToClient(clientProcess.MemoryManager);

            if (clientResult != KernelResult.Success)
            {
                CleanUpForError();

                return serverResult;
            }

            //Copy header.
            System.Device.Memory.WriteUInt32((long)clientMsg.DramAddress + 0, word0);
            System.Device.Memory.WriteUInt32((long)clientMsg.DramAddress + 4, word1);

            //Copy handles.
            uint offset;

            if (header.HasHandles)
            {
                offset = 3;

                System.Device.Memory.WriteUInt32((long)clientMsg.DramAddress + 8, word2);

                if (header.HasPid)
                {
                    System.Device.Memory.WriteInt64((long)clientMsg.DramAddress + offset * 4, serverProcess.Pid);

                    offset += 2;
                }

                for (int index = 0; index < header.CopyHandlesCount; index++)
                {
                    int newHandle = 0;

                    int handle = serverProcess.CpuMemory.ReadInt32((long)serverMsg.Address + offset * 4);

                    if (handle != 0)
                    {
                        GetCopyObjectHandle(serverThread, clientProcess, handle, out newHandle);
                    }

                    System.Device.Memory.WriteInt32((long)clientMsg.DramAddress + offset * 4, newHandle);

                    offset++;
                }

                for (int index = 0; index < header.MoveHandlesCount; index++)
                {
                    int newHandle = 0;

                    int handle = serverProcess.CpuMemory.ReadInt32((long)serverMsg.Address + offset * 4);

                    if (handle != 0)
                    {
                        if (clientResult == KernelResult.Success)
                        {
                            clientResult = GetMoveObjectHandle(serverProcess, clientProcess, handle, out newHandle);
                        }
                        else
                        {
                            serverProcess.HandleTable.CloseHandle(handle);
                        }
                    }

                    System.Device.Memory.WriteInt32((long)clientMsg.DramAddress + offset * 4, newHandle);

                    offset++;
                }
            }
            else
            {
                offset = 2;
            }

            //Copy pointer/receive list buffers.
            for (int index = 0; index < header.PointerBuffersCount; index++)
            {
                ulong pointerDesc = serverProcess.CpuMemory.ReadUInt64((long)serverMsg.Address + offset * 4);

                PointerBufferDesc descriptor = new PointerBufferDesc(pointerDesc);

                if (descriptor.BufferSize != 0)
                {
                    clientResult = GetReceiveListAddress(
                        descriptor,
                        clientMsg,
                        clientHeader.ReceiveListType,
                        header.MessageSizeInWords,
                        receiveList,
                        out ulong recvListBufferAddress);

                    if (clientResult != KernelResult.Success)
                    {
                        CleanUpForError();

                        return serverResult;
                    }

                    clientResult = clientProcess.MemoryManager.CopyDataFromCurrentProcess(
                        recvListBufferAddress,
                        descriptor.BufferSize,
                        MemoryState.IsPoolAllocated,
                        MemoryState.IsPoolAllocated,
                        MemoryPermission.Read,
                        MemoryAttribute.Uncached,
                        MemoryAttribute.None,
                        descriptor.BufferAddress);

                    if (clientResult != KernelResult.Success)
                    {
                        CleanUpForError();

                        return serverResult;
                    }
                }

                offset += 2;
            }

            //Set send, receive and exchange buffer descriptors to zero.
            uint totalBuffersCount =
                header.SendBuffersCount    +
                header.ReceiveBuffersCount +
                header.ExchangeBuffersCount;

            for (int index = 0; index < totalBuffersCount; index++)
            {
                long dstDescAddress = (long)clientMsg.DramAddress + offset * 4;

                System.Device.Memory.WriteUInt32(dstDescAddress + 0, 0);
                System.Device.Memory.WriteUInt32(dstDescAddress + 4, 0);
                System.Device.Memory.WriteUInt32(dstDescAddress + 8, 0);

                offset += 3;
            }

            //Copy raw data.
            if (header.RawDataSizeInWords != 0)
            {
                ulong copyDst = clientMsg.Address + offset * 4;
                ulong copySrc = serverMsg.Address + offset * 4;

                ulong copySize = header.RawDataSizeInWords * 4;

                if (serverMsg.IsCustom || clientMsg.IsCustom)
                {
                    MemoryPermission permission = clientMsg.IsCustom
                        ? MemoryPermission.None
                        : MemoryPermission.Read;

                    clientResult = clientProcess.MemoryManager.CopyDataFromCurrentProcess(
                        copyDst,
                        copySize,
                        MemoryState.IsPoolAllocated,
                        MemoryState.IsPoolAllocated,
                        permission,
                        MemoryAttribute.Uncached,
                        MemoryAttribute.None,
                        copySrc);
                }
                else
                {
                    copyDst = clientProcess.MemoryManager.GetDramAddressFromVa(copyDst);
                    copySrc = serverProcess.MemoryManager.GetDramAddressFromVa(copySrc);

                    System.Device.Memory.Copy(copyDst, copySrc, copySize);
                }
            }

            //Unmap buffers from server.
            clientResult = request.BufferDescriptorTable.UnmapServerBuffers(serverProcess.MemoryManager);

            if (clientResult != KernelResult.Success)
            {
                CleanUpForError();

                return serverResult;
            }

            WakeClient(request, clientResult);

            return serverResult;
        }

        private MessageHeader GetClientMessageHeader(Message clientMsg)
        {
            uint word0 = System.Device.Memory.ReadUInt32((long)clientMsg.DramAddress + 0);
            uint word1 = System.Device.Memory.ReadUInt32((long)clientMsg.DramAddress + 4);
            uint word2 = System.Device.Memory.ReadUInt32((long)clientMsg.DramAddress + 8);

            return new MessageHeader(word0, word1, word2);
        }

        private KernelResult GetCopyObjectHandle(
            KThread  srcThread,
            KProcess dstProcess,
            int      srcHandle,
            out int  dstHandle)
        {
            dstHandle = 0;

            KProcess srcProcess = srcThread.Owner;

            KAutoObject obj;

            if (srcHandle == KHandleTable.SelfProcessHandle)
            {
                obj = srcProcess;
            }
            else if (srcHandle == KHandleTable.SelfThreadHandle)
            {
                obj = srcThread;
            }
            else
            {
                obj = srcProcess.HandleTable.GetObject<KAutoObject>(srcHandle);
            }

            if (obj != null)
            {
                return dstProcess.HandleTable.GenerateHandle(obj, out dstHandle);
            }
            else
            {
                return KernelResult.InvalidHandle;
            }
        }

        private KernelResult GetMoveObjectHandle(
            KProcess srcProcess,
            KProcess dstProcess,
            int      srcHandle,
            out int  dstHandle)
        {
            dstHandle = 0;

            KAutoObject obj = srcProcess.HandleTable.GetObject<KAutoObject>(srcHandle);

            if (obj != null)
            {
                KernelResult result = dstProcess.HandleTable.GenerateHandle(obj, out dstHandle);

                srcProcess.HandleTable.CloseHandle(srcHandle);

                return result;
            }
            else
            {
                return KernelResult.InvalidHandle;
            }
        }

        private ulong[] GetReceiveList(Message message, uint recvListType, uint recvListOffset)
        {
            int recvListSize = 0;

            if (recvListType >= 3)
            {
                recvListSize = (int)recvListType - 2;
            }
            else if (recvListType == 2)
            {
                recvListSize = 1;
            }

            ulong[] receiveList = new ulong[recvListSize];

            long recvListAddress = (long)message.DramAddress + recvListOffset;

            for (int index = 0; index < recvListSize; index++)
            {
                receiveList[index] = System.Device.Memory.ReadUInt64(recvListAddress + index * 8);
            }

            return receiveList;
        }

        private KernelResult GetReceiveListAddress(
            PointerBufferDesc descriptor,
            Message           message,
            uint              recvListType,
            uint              messageSizeInWords,
            ulong[]           receiveList,
            out ulong         address)
        {
            ulong recvListBufferAddress = address = 0;

            if (recvListType == 0)
            {
                return KernelResult.OutOfResource;
            }
            else if (recvListType == 1 || recvListType == 2)
            {
                ulong recvListBaseAddr;
                ulong recvListEndAddr;

                if (recvListType == 1)
                {
                    recvListBaseAddr = message.Address + messageSizeInWords * 4;
                    recvListEndAddr  = message.Address + message.Size;
                }
                else /* if (recvListType == 2) */
                {
                    ulong packed = receiveList[0];

                    recvListBaseAddr = packed & 0x7fffffffff;

                    uint size = (uint)(packed >> 48);

                    if (size == 0)
                    {
                        return KernelResult.OutOfResource;
                    }

                    recvListEndAddr = recvListBaseAddr + size;
                }

                recvListBufferAddress = BitUtils.AlignUp(recvListBaseAddr + descriptor.ReceiveIndex, 0x10);

                if (recvListBufferAddress + descriptor.BufferSize <= recvListBufferAddress ||
                    recvListBufferAddress + descriptor.BufferSize >  recvListEndAddr)
                {
                    return KernelResult.OutOfResource;
                }
            }
            else /* if (recvListType > 2) */
            {
                if (descriptor.ReceiveIndex >= receiveList.Length)
                {
                    return KernelResult.OutOfResource;
                }

                ulong packed = receiveList[descriptor.ReceiveIndex];

                recvListBufferAddress = packed & 0x7fffffffff;

                uint transferSize = (uint)(packed >> 48);

                if (recvListBufferAddress == 0 || transferSize == 0 || transferSize < descriptor.BufferSize)
                {
                    return KernelResult.OutOfResource;
                }
            }

            address = recvListBufferAddress;

            return KernelResult.Success;
        }

        private void CloseAllHandles(Message message, MessageHeader header, KProcess process)
        {
            if (header.HasHandles)
            {
                uint totalHandeslCount = header.CopyHandlesCount + header.MoveHandlesCount;

                uint offset = 3;

                if (header.HasPid)
                {
                    process.CpuMemory.WriteInt64((long)message.Address + offset * 4, 0);

                    offset += 2;
                }

                for (int index = 0; index < totalHandeslCount; index++)
                {
                    int handle = process.CpuMemory.ReadInt32((long)message.Address + offset * 4);

                    if (handle != 0)
                    {
                        process.HandleTable.CloseHandle(handle);

                        process.CpuMemory.WriteInt32((long)message.Address + offset * 4, 0);
                    }

                    offset++;
                }
            }
        }

        public override bool IsSignaled()
        {
            if (_parent.ClientSession.State != ChannelState.Open)
            {
                return true;
            }

            return _requests.Count != 0 && _activeRequest == null;
        }

        protected override void Destroy()
        {
            _parent.DisconnectServer();

            CancelAllRequests(KernelResult.PortRemoteClosed);

            _parent.DecrementReferenceCount();
        }

        private void CancelAllRequests(KernelResult result)
        {
            System.CriticalSection.Enter();

            if (_activeRequest != null)
            {
                KSessionRequest request = _activeRequest;

                _activeRequest = null;

                CancelRequest(request, result);

                System.CriticalSection.Leave();
            }
            else
            {
                System.CriticalSection.Leave();
            }

            while (PickRequest(out KSessionRequest request))
            {
                CancelRequest(request, result);
            }
        }

        private bool PickRequest(out KSessionRequest request)
        {
            request = null;

            System.CriticalSection.Enter();

            bool hasRequest = _requests.First != null;

            if (hasRequest)
            {
                request = _requests.First.Value;

                _requests.RemoveFirst();
            }

            System.CriticalSection.Leave();

            return hasRequest;
        }

        private void CancelRequest(KSessionRequest request, KernelResult result)
        {
            KProcess clientProcess = request.ClientThread.Owner;
            KProcess serverProcess = request.ServerProcess;

            KernelResult unmapResult = KernelResult.Success;

            if (serverProcess != null)
            {
                unmapResult = request.BufferDescriptorTable.UnmapServerBuffers(serverProcess.MemoryManager);
            }

            if (unmapResult == KernelResult.Success)
            {
                request.BufferDescriptorTable.RestoreClientBuffers(clientProcess.MemoryManager);
            }

            WakeClient(request, result);
        }

        private void WakeClient(KSessionRequest request, KernelResult result)
        {
            KThread  clientThread  = request.ClientThread;
            KProcess clientProcess = clientThread.Owner;

            if (request.AsyncEvent != null)
            {
                ulong address = clientProcess.MemoryManager.GetDramAddressFromVa(request.CustomCmdBuffAddr);

                System.Device.Memory.WriteInt64((long)address + 0, 0);
                System.Device.Memory.WriteInt32((long)address + 8, (int)result);

                clientProcess.MemoryManager.UnborrowIpcBuffer(
                    request.CustomCmdBuffAddr,
                    request.CustomCmdBuffSize);

                request.AsyncEvent.Signal();
            }
            else
            {
                System.CriticalSection.Enter();

                if ((clientThread.SchedFlags & ThreadSchedState.LowMask) == ThreadSchedState.Paused)
                {
                    clientThread.SignaledObj   = null;
                    clientThread.ObjSyncResult = result;

                    clientThread.Reschedule(ThreadSchedState.Running);
                }

                System.CriticalSection.Leave();
            }
        }
    }
}