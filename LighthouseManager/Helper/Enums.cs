using System;
using System.Text.RegularExpressions;

namespace LighthouseManager.Helper
{
    public static class Extensions
    {
        public static ulong ToMacUlong(this string macAddress)
        {
            var hex = macAddress.Replace(":", "");
            return Convert.ToUInt64(hex, 16);
        }

        public static string ToMacString(this ulong macAddress)
        {
            var regex = "(.{2})(.{2})(.{2})(.{2})(.{2})(.{2})";
            var replace = "$1:$2:$3:$4:$5:$6";
            return Regex.Replace(macAddress.ToString("X"), regex, replace);
        }
    }
}