﻿using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PhysicalMonitorHandle = System.IntPtr;

namespace SetBrightness
{
    internal partial class DdcCiMonitor : Monitor
    {
        // refer MCCS vcp codes: https://milek7.pl/ddcbacklight/mccs.pdf

        [DllImport("Dxva2.dll", SetLastError = true)]
        private static extern bool SetVCPFeature(PhysicalMonitorHandle hMonitor, byte bVcpCode, uint dwNewValue);

        [DllImport("Dxva2.dll")]
        private static extern bool GetMonitorCapabilities(PhysicalMonitorHandle hMonitor,
            out PdwMonitorCapabilitiesFlag pdwMonitorCapabilities,
            out PdwSupportedColorTemperaturesFlag pdwSupportedColorTemperatures);

        [DllImport("Dxva2.dll")]
        private static extern bool GetVCPFeatureAndVCPFeatureReply(PhysicalMonitorHandle hMonitor, byte bVcpCode,
            out LpmcVcpCodeType pvct, out uint pdwCurrentValue, out uint pdwMaximumValue);

        [DllImport("Dxva2.dll")]
        private static extern bool GetCapabilitiesStringLength(
            PhysicalMonitorHandle hMonitor, out uint pdwCapabilitiesStringLengthInCharacters);

        [DllImport("Dxva2.dll")]
        private static extern bool CapabilitiesRequestAndCapabilitiesReply(
            PhysicalMonitorHandle hMonitor,
            [MarshalAs(UnmanagedType.LPStr)] [Out] StringBuilder pszAsciiCapabilitiesString,
            uint dwCapabilitiesStringLengthInCharacters);

        private enum LpmcVcpCodeType
        {
            McMomentary,
            McSetParameter
        }

        private const byte VcpLuminanceCode = 0x10;
        private const byte VcpContrastCode = 0x12;

        private bool _isLowLevel;

        private readonly PhysicalMonitorHandle _physicalMonitorHandle;

        public DdcCiMonitor(IntPtr physicalMonitorHandle, string name)
        {
            Type = MonitorType.DdcCiMonitor;
            _physicalMonitorHandle = physicalMonitorHandle;
            Name = name;
            FigureOutInfo();
        }

        private void FigureOutInfo()
        {
            PdwMonitorCapabilitiesFlag highFlag;
            PdwSupportedColorTemperaturesFlag _;
            CanUse = GetMonitorCapabilities(_physicalMonitorHandle, out highFlag, out _);
            if (!CanUse)
            {
                return;
            }

            if (highFlag.HasFlag(PdwMonitorCapabilitiesFlag.McCapsBrightness))
            {
                if (!highFlag.HasFlag(PdwMonitorCapabilitiesFlag.McCapsContrast))
                {
                    return;
                }

                SupportContrast = true;
            }
            else
            {
                _isLowLevel = true;
                TestLowLevelCapabilities();
            }
        }

        private void TestLowLevelCapabilities()
        {
            uint strLength;
            if (!GetCapabilitiesStringLength(_physicalMonitorHandle, out strLength))
            {
                return;
            }

            var str = new StringBuilder((int) strLength);
            CapabilitiesRequestAndCapabilitiesReply(_physicalMonitorHandle, str, strLength);

            var capabilitiesStr = str.ToString();
            var vcpIndex = capabilitiesStr.IndexOf("vcp", StringComparison.OrdinalIgnoreCase);
            string vcpStr;
            try
            {
                vcpStr = capabilitiesStr.Substring(vcpIndex + 4);
            }
            catch (ArgumentOutOfRangeException)
            {
                return;
            }

            var ignoreBrace = 0;
            var vcpCode = "";
            foreach (var c in vcpStr)
            {
                if (c == "("[0])
                {
                    if (CheckVcpCode(ref vcpCode))
                    {
                        return;
                    }

                    ignoreBrace++;
                }
                else if (c == ")"[0])
                {
                    if (CheckVcpCode(ref vcpCode))
                    {
                        return;
                    }

                    ignoreBrace--;
                }
                else if (ignoreBrace > 0)
                {
                    continue;
                }
                else if (c == " "[0])
                {
                    if (CheckVcpCode(ref vcpCode))
                    {
                        return;
                    }
                }
                else
                {
                    vcpCode += c;
                }

                if (ignoreBrace < 0)
                {
                    break;
                }
            }
        }

        private bool CheckVcpCode(ref string vcpCode)
        {
            switch (vcpCode)
            {
                case "10":
                    CanUse = true;
                    break;
                case "12":
                    SupportContrast = true;
                    break;
            }

            vcpCode = "";
            return CanUse && SupportContrast;
        }

        private delegate bool NativeHighLevelGet(PhysicalMonitorHandle hMonitor,
            ref short min, ref short current, ref short max);

        [DllImport("dxva2.dll")]
        private static extern bool GetMonitorBrightness(PhysicalMonitorHandle hMonitor,
            ref short pdwMinimumBrightness, ref short pdwCurrentBrightness, ref short pdwMaximumBrightness);

        [DllImport("dxva2.dll")]
        private static extern bool SetMonitorBrightness(PhysicalMonitorHandle hMonitor, uint brightness);

        [DllImport("dxva2.dll")]
        private static extern bool GetMonitorContrast(PhysicalMonitorHandle hMonitor, ref short pdwMinimumContrast,
            ref short pdwCurrentContrast, ref short pdwMaximumContrast);

        [DllImport("dxva2.dll")]
        private static extern bool SetMonitorContrast(PhysicalMonitorHandle hMonitor, uint brightness);

        private static void RestrictValue(ref int value)
        {
            value = Math.Max(0, value);
            value = Math.Min(100, value);
        }

        public override void SetBrightness(int brightness)
        {
            RestrictValue(ref brightness);
            if (_isLowLevel)
            {
                SetVCPFeature(_physicalMonitorHandle, VcpLuminanceCode, (uint) brightness);
            }
            else
            {
                SetMonitorBrightness(_physicalMonitorHandle, (uint) brightness);
            }
        }

        public override void SetContrast(int contrast)
        {
            RestrictValue(ref contrast);
            if (_isLowLevel)
            {
                SetVCPFeature(_physicalMonitorHandle, VcpContrastCode, (byte) contrast);
            }
            else
            {
                SetMonitorContrast(_physicalMonitorHandle, (uint) contrast);
            }
        }

        public override int GetBrightness()
        {
            return _isLowLevel
                ? LowLevelGetCurrentValue(VcpLuminanceCode)
                : HighLevelGetCurrentValue(GetMonitorBrightness);
        }

        public override int GetContrast()
        {
            return _isLowLevel
                ? LowLevelGetCurrentValue(VcpContrastCode)
                : HighLevelGetCurrentValue(GetMonitorContrast);
        }

        private bool _tested;

        public override bool IsSameMonitor(Monitor monitor)
        {
            // not found effective way to detect the same monitor handle
            _tested = true;
            return false;
        }

        public override bool IsValide()
        {
            if (_tested)
            {
                return false;
            }

            if (!_isLowLevel)
            {
                var values = new short[3];
                return GetMonitorBrightness(_physicalMonitorHandle, ref values[0], ref values[1], ref values[2]);
            }

            LpmcVcpCodeType pvct;
            uint currentValue, max;
            return GetVCPFeatureAndVCPFeatureReply(_physicalMonitorHandle, VcpLuminanceCode,
                out pvct, out currentValue, out max);
        }

        private int LowLevelGetCurrentValue(byte code)
        {
            LpmcVcpCodeType pvct;
            uint currentValue, max;
            GetVCPFeatureAndVCPFeatureReply(_physicalMonitorHandle, code, out pvct, out currentValue, out max);
            return (int) currentValue;
        }

        private int HighLevelGetCurrentValue(NativeHighLevelGet func)
        {
            var values = new short[3];
            if (!func(_physicalMonitorHandle, ref values[0], ref values[1], ref values[2]))
            {
                throw new HighLevelPhysicalHandleInvalidException();
            }

            return values[1];
        }

        [DllImport("Dxva2.dll")]
        private static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

        ~DdcCiMonitor()
        {
            DestroyPhysicalMonitor(_physicalMonitorHandle);
        }
    }

    internal class HighLevelPhysicalHandleInvalidException : InvalidMonitorException
    {
        public override string ToString()
        {
            return "HighLevelPhysicalHandleInvalidException";
        }
    }
}