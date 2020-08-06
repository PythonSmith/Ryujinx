using System;

namespace Ryujinx.Configuration
{
    internal class SupportedSinceAttribute : Attribute
    {
        public SupportedSinceAttribute(int version)
        {
            Version = version;
        }

        public int Version { get; }
    }
}