using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FireLauncher.Interop
{
    internal static class WindowBlur
    {
        public static void TryEnable(Window window)
        {
            if (window == null)
            {
                return;
            }

            window.SourceInitialized += (_, __) => Apply(window);
        }

        private static void Apply(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var accent = new AccentPolicy
            {
                AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND,
                GradientColor = unchecked((int)0xD0000000)
            };

            var accentSize = Marshal.SizeOf(accent);
            var accentPointer = Marshal.AllocHGlobal(accentSize);

            try
            {
                Marshal.StructureToPtr(accent, accentPointer, false);

                var data = new WindowCompositionAttributeData
                {
                    Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                    SizeOfData = accentSize,
                    Data = accentPointer
                };

                SetWindowCompositionAttribute(hwnd, ref data);
            }
            catch
            {
                // Ignore unsupported Windows builds and keep the normal WPF fallback visuals.
            }
            finally
            {
                Marshal.FreeHGlobal(accentPointer);
            }
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public WindowCompositionAttribute Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        private enum WindowCompositionAttribute
        {
            WCA_ACCENT_POLICY = 19
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public AccentState AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        private enum AccentState
        {
            ACCENT_DISABLED = 0,
            ACCENT_ENABLE_BLURBEHIND = 3
        }
    }
}
