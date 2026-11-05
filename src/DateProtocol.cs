using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HistDataExport {
internal static class DateProtocol {
    [StructLayout(LayoutKind.Sequential)] struct SystemTime {
        internal ushort Year,Month,DayOfWeek,Day,Hour,Minute,Second,Milliseconds;
    }
    [DllImport("kernel32.dll",EntryPoint="GetDateFormatW",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern int GetDate(uint locale,uint flags,ref SystemTime date,string format,StringBuilder text,int capacity);
    [DllImport("kernel32.dll",EntryPoint="GetLocaleInfoW",CharSet=CharSet.Unicode)]
    static extern int LocaleInfo(uint locale,uint type,StringBuilder text,int capacity);
    internal static string WindowsPattern {
        get { var b=new StringBuilder(256); return LocaleInfo(0x400,0x1f,b,b.Capacity)>0?b.ToString():CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern; }
    }
    internal static string Format(DateTime date,string mode) {
        if(mode!="Windows") return date.ToString(mode,CultureInfo.InvariantCulture);
        return FormatWithWindowsPattern(date,null);
    }
    internal static string FormatWithWindowsPattern(DateTime date,string pattern) {
        var value=new SystemTime { Year=(ushort)date.Year,Month=(ushort)date.Month,Day=(ushort)date.Day,DayOfWeek=(ushort)date.DayOfWeek };
        var b=new StringBuilder(256);
        if(GetDate(0x400,0,ref value,pattern,b,b.Capacity)==0) throw new IOException("无法读取 Windows 当前短日期格式，错误码："+Marshal.GetLastWin32Error());
        return b.ToString();
    }
}
}
