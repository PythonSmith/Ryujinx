using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Utilities;
using Ryujinx.Configuration.Hid;
using Ryujinx.Configuration.System;
using Ryujinx.Configuration.Ui;

namespace Ryujinx.Configuration
{
    public class ConfigurationFileFormat
    {
        /// <summary>
        /// The current version of the file format
        /// </summary>
        public const int CurrentVersion = 12;

        public int Version { get; set; }

        /// <summary>
        /// Resolution Scale. An integer scale applied to applicable render targets. Values 1-4, or -1 to use a custom floating point scale instead.
        /// </summary>
        [SupportedSince(11)]
        public int ResScale { get; set; } = 1;

        /// <summary>
        /// Custom Resolution Scale. A custom floating point scale applied to applicable render targets. Only active when Resolution Scale is -1.
        /// </summary>
        [SupportedSince(11)]
        public float ResScaleCustom { get; set; } = 1.0f;

        /// <summary>
        /// Max Anisotropy. Values range from 0 - 16. Set to -1 to let the game decide.
        /// </summary>
        [SupportedSince(4)]
        public float MaxAnisotropy { get; set; } = -1;

        /// <summary>
        /// Dumps shaders in this local directory
        /// </summary>
        public string GraphicsShadersDumpPath { get; set; } = "";

        /// <summary>
        /// Enables printing debug log messages
        /// </summary>
        public bool LoggingEnableDebug { get; set; } = false;

        /// <summary>
        /// Enables printing stub log messages
        /// </summary>
        public bool LoggingEnableStub { get; set; } = true;

        /// <summary>
        /// Enables printing info log messages
        /// </summary>
        public bool LoggingEnableInfo { get; set; } = true;

        /// <summary>
        /// Enables printing warning log messages
        /// </summary>
        public bool LoggingEnableWarn { get; set; } = true;

        /// <summary>
        /// Enables printing error log messages
        /// </summary>
        public bool LoggingEnableError { get; set; } = true;

        /// <summary>
        /// Enables printing guest log messages
        /// </summary>
        public bool LoggingEnableGuest { get; set; } = true;

        /// <summary>
        /// Enables printing FS access log messages
        /// </summary>
        public bool LoggingEnableFsAccessLog { get; set; } = false;

        /// <summary>
        /// Controls which log messages are written to the log targets
        /// </summary>
        public LogClass[] LoggingFilteredClasses { get; set; } = new LogClass[] { };

        /// <summary>
        /// Change Graphics API debug log level
        /// </summary>
        [SupportedSince(12)]
        public GraphicsDebugLevel LoggingGraphicsDebugLevel { get; set; } = GraphicsDebugLevel.None;

        /// <summary>
        /// Enables or disables logging to a file on disk
        /// </summary>
        public bool EnableFileLog { get; set; } = true;

        /// <summary>
        /// Change System Language
        /// </summary>
        public Language SystemLanguage { get; set; } = Language.AmericanEnglish;

        /// <summary>
        /// Change System Region
        /// </summary>
        [SupportedSince(2)]
        public Region SystemRegion { get; set; } = Region.USA;

        /// <summary>
        /// Change System TimeZone
        /// </summary>
        [SupportedSince(3)]
        public string SystemTimeZone { get; set; } = "UTC";

        /// <summary>
        /// Change System Time Offset in seconds
        /// </summary>
        [SupportedSince(5)]
        public long SystemTimeOffset { get; set; } = 0;

        /// <summary>
        /// Enables or disables Docked Mode
        /// </summary>
        public bool DockedMode { get; set; } = false;

        /// <summary>
        /// Enables or disables Discord Rich Presence
        /// </summary>
        public bool EnableDiscordIntegration { get; set; } = true;

        /// <summary>
        /// Enables or disables Vertical Sync
        /// </summary>
        public bool EnableVsync { get; set; } = true;

        /// <summary>
        /// Enables or disables multi-core scheduling of threads
        /// </summary>
        public bool EnableMulticoreScheduling { get; set; } = true;

        /// <summary>
        /// Enables or disables profiled translation cache persistency
        /// </summary>
        [SupportedSince(8)]
        public bool EnablePtc { get; set; } = false;

        /// <summary>
        /// Enables integrity checks on Game content files
        /// </summary>
        public bool EnableFsIntegrityChecks { get; set; } = true;

        /// <summary>
        /// Enables FS access log output to the console. Possible modes are 0-3
        /// </summary>
        public int FsGlobalAccessLogMode { get; set; } = 0;

        /// <summary>
        /// The selected audio backend
        /// </summary>
        [SupportedSince(10)]
        public AudioBackend AudioBackend { get; set; } = AudioBackend.OpenAl;

        /// <summary>
        /// Enable or disable ignoring missing services
        /// </summary>
        public bool IgnoreMissingServices { get; set; } = false;

        /// <summary>
        /// Used to toggle columns in the GUI
        /// </summary>
        public GuiColumns GuiColumns { get; set; } = new GuiColumns
        {
            FavColumn = true,
            IconColumn = true,
            AppColumn = true,
            DevColumn = true,
            VersionColumn = true,
            TimePlayedColumn = true,
            LastPlayedColumn = true,
            FileExtColumn = true,
            FileSizeColumn = true,
            PathColumn = true,
        };

        /// <summary>
        /// Used to configure column sort settings in the GUI
        /// </summary>
        [SupportedSince(9)]
        public ColumnSort ColumnSort { get; set; } = new ColumnSort
        {
            SortColumnId = 0,
            SortAscending = false
        };

        /// <summary>
        /// A list of directories containing games to be used to load games into the games list
        /// </summary>
        public List<string> GameDirs { get; set; } = new List<string>();

        /// <summary>
        /// Enable or disable custom themes in the GUI
        /// </summary>
        public bool EnableCustomTheme { get; set; } = false;

        /// <summary>
        /// Path to custom GUI theme
        /// </summary>
        public string CustomThemePath { get; set; } = "";

        /// <summary>
        /// Enable or disable keyboard support (Independent from controllers binding)
        /// </summary>
        public bool EnableKeyboard { get; set; } = false;

        /// <summary>
        /// Hotkey Keyboard Bindings
        /// </summary>
        [SupportedSince(9)]
        public KeyboardHotkeys Hotkeys { get; set; } = new KeyboardHotkeys
        {
            ToggleVsync = Key.Tab
        };

        /// <summary>
        /// Keyboard control bindings
        /// </summary>
        [SupportedSince(6)]
        public List<KeyboardConfig> KeyboardConfig { get; set; } = new List<KeyboardConfig>
                {
                    new KeyboardConfig
                    {
                        Index          = 0,
                        ControllerType = ControllerType.JoyconPair,
                        PlayerIndex    = PlayerIndex.Player1,
                        LeftJoycon     = new NpadKeyboardLeft
                        {
                            StickUp     = Key.W,
                            StickDown   = Key.S,
                            StickLeft   = Key.A,
                            StickRight  = Key.D,
                            StickButton = Key.F,
                            DPadUp      = Key.Up,
                            DPadDown    = Key.Down,
                            DPadLeft    = Key.Left,
                            DPadRight   = Key.Right,
                            ButtonMinus = Key.Minus,
                            ButtonL     = Key.E,
                            ButtonZl    = Key.Q,
                            ButtonSl    = Key.Home,
                            ButtonSr    = Key.End
                        },
                        RightJoycon    = new NpadKeyboardRight
                        {
                            StickUp     = Key.I,
                            StickDown   = Key.K,
                            StickLeft   = Key.J,
                            StickRight  = Key.L,
                            StickButton = Key.H,
                            ButtonA     = Key.Z,
                            ButtonB     = Key.X,
                            ButtonX     = Key.C,
                            ButtonY     = Key.V,
                            ButtonPlus  = Key.Plus,
                            ButtonR     = Key.U,
                            ButtonZr    = Key.O,
                            ButtonSl    = Key.PageUp,
                            ButtonSr    = Key.PageDown
                        }
                    }
                };

        /// <summary>
        /// Controller control bindings
        /// </summary>
        [SupportedSince(6)]
        public List<ControllerConfig> ControllerConfig { get; set; } = new List<ControllerConfig>();

        /// <summary>
        /// Loads a configuration file from disk
        /// </summary>
        /// <param name="path">The path to the JSON configuration file</param>
        public static ConfigurationFileFormat Load(string path)
        {
            return JsonHelper.DeserializeFromFile<ConfigurationFileFormat>(path);
        }

        /// <summary>
        /// Save a configuration file to disk
        /// </summary>
        /// <param name="path">The path to the JSON configuration file</param>
        public void SaveConfig(string path)
        {
            File.WriteAllText(path, JsonHelper.Serialize(this, true));
        }
    }
}