using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace HistDataExport {
internal sealed class DebugLogFile : IDisposable {
    readonly object sync=new object(); StreamWriter writer; long sequence;
    internal readonly string SessionId=Guid.NewGuid().ToString("N");
    internal string PathName { get; private set; }
    internal string LastError { get; private set; }
    internal DebugLogFile(string directory) : this(null,directory) {}
    internal DebugLogFile(DateTime? timestamp,string directory) {
            try {
                Directory.CreateDirectory(directory);
                string stem="操作日志-"+(timestamp??DateTime.Now).ToString("yyyyMMdd_HHmmss_fff",CultureInfo.InvariantCulture);
                for(int collision=0;collision<10000;collision++) {
                    string path=Path.Combine(directory,stem+(collision==0?"":"_"+collision.ToString("D3"))+".jsonl");
                    try { writer=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.ReadWrite),new UTF8Encoding(false)); }
                    catch(IOException) { if(File.Exists(path)) continue; throw; }
                    writer.AutoFlush=true; PathName=path;
                    Write("INFO","SESSION_START","LogDirectory="+directory,"",Thread.CurrentThread.ManagedThreadId);
                    return;
                }
                throw new IOException("日志同名次数过多。");
            } catch(Exception ex) { LastError="无法在指定日志目录创建操作日志："+directory+"\r\n"+ex.Message; }
    }
    internal static string Quote(string value) {
        var b=new StringBuilder("\"");
        foreach(char c in value??"") {
            switch(c) {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\r': b.Append("\\r"); break;
                case '\n': b.Append("\\n"); break;
                case '\t': b.Append("\\t"); break;
                default: if(c<32) b.Append("\\u"+((int)c).ToString("x4")); else b.Append(c); break;
            }
        }
        return b.Append('"').ToString();
    }
    internal void Write(string level,string eventName,string message,string operation,int thread) {
        lock(sync) {
            if(writer==null) return;
            try {
                writer.WriteLine("{\"seq\":"+(++sequence)+",\"time\":"+Quote(DateTimeOffset.Now.ToString("o",CultureInfo.InvariantCulture))+",\"session\":"+Quote(SessionId)+",\"operation\":"+Quote(operation)+",\"thread\":"+thread+",\"level\":"+Quote(level)+",\"event\":"+Quote(eventName)+",\"message\":"+Quote(message)+"}");
            } catch(Exception ex) { LastError="日志写入失败："+ex.Message; }
        }
    }
    internal string ReadSnapshot() {
        lock(sync) {
            if(PathName==null) throw new IOException(LastError??"本次没有可读取的日志文件。");
            if(writer!=null) writer.Flush();
            using(var input=new FileStream(PathName,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
            using(var reader=new StreamReader(input,Encoding.UTF8)) return reader.ReadToEnd();
        }
    }
    public void Dispose() {
        lock(sync) {
            if(writer==null) return;
            Write("INFO","SESSION_END","关闭日志记录。","",Thread.CurrentThread.ManagedThreadId);
            try { writer.Dispose(); } catch(Exception ex) { LastError="日志关闭失败："+ex.Message; }
            writer=null;
        }
    }
}

internal static class SessionLog {
    static DebugLogFile file;
    [ThreadStatic] static string operation;
    internal static string Operation { get { return operation; } set { operation=value; } }
    internal static string PathName { get { return file==null?null:file.PathName; } }
    internal static string LastError { get { return file==null?null:file.LastError; } }
    internal static string DefaultDirectory { get { return Path.Combine(Application.StartupPath,"logs"); } }
    internal static void Start(string directory) { file=new DebugLogFile(directory); }
    internal static void End() { if(file!=null) file.Dispose(); }
    internal static string ReadSnapshot() { if(file==null) throw new IOException("未初始化日志。"); return file.ReadSnapshot(); }
    internal static void Write(string level,string name,string message) {
        if(file!=null) file.Write(level,name,message,operation,Thread.CurrentThread.ManagedThreadId);
    }
    internal static void BeginOperation(string kind) {
        operation=kind+"_"+Guid.NewGuid().ToString("N"); Write("INFO","OPERATION_BEGIN",kind);
    }
    internal static void Exception(string where,Exception ex) {
        var text=new StringBuilder(ex.ToString());
        for(Exception current=ex;current!=null;current=current.InnerException) {
            text.AppendLine("\r\nExceptionType="+current.GetType().FullName);
            foreach(DictionaryEntry item in current.Data) text.AppendLine("Data["+item.Key+"]="+item.Value);
        }
        Write("ERROR",where,text.ToString());
    }
    internal static void EnvironmentSnapshot() {
        try {
            using(var process=Process.GetCurrentProcess()) {
                Write("INFO","ENVIRONMENT","Exe="+Application.ExecutablePath+"\r\nVersion="+FileVersionInfo.GetVersionInfo(Application.ExecutablePath).FileVersion+
                    "\r\nOS="+Environment.OSVersion+"\r\nCLR="+Environment.Version+"\r\nOS64="+Environment.Is64BitOperatingSystem+" Process64="+Environment.Is64BitProcess+
                    "\r\nMachine="+Environment.MachineName+" User="+Environment.UserDomainName+"\\"+Environment.UserName+" PID="+process.Id+" SessionId="+process.SessionId+
                    "\r\nCurrentDirectory="+Environment.CurrentDirectory+"\r\nArguments="+string.Join(" | ",Environment.GetCommandLineArgs())+
                    "\r\nCulture="+CultureInfo.CurrentCulture.Name+" UICulture="+CultureInfo.CurrentUICulture.Name+" ANSI="+DdeClient.Ansi.CodePage+
                    "\r\nWindowsShortDate="+DateProtocol.WindowsPattern+" TodayAsWindows="+DateProtocol.Format(DateTime.Today,"Windows")+
                    "\r\nTimeZone="+TimeZoneInfo.Local.Id+" UTC="+DateTime.UtcNow.ToString("o")+"\r\nInputStore="+InputStore.DefaultPath);
            }
            using(var identity=WindowsIdentity.GetCurrent()) Write("INFO","IDENTITY","Identity="+identity.Name+" AdministratorToken="+new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
            try {
                using(var hive=Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,Environment.Is64BitOperatingSystem?Microsoft.Win32.RegistryView.Registry64:Microsoft.Win32.RegistryView.Registry32))
                using(var key=hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    if(key!=null) Write("INFO","WINDOWS_VERSION",string.Join(" | ",new[]{"ProductName","DisplayVersion","ReleaseId","CurrentBuildNumber","UBR"}.Select(n=>n+"="+key.GetValue(n))));
                using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\International"))
                    if(key!=null) Write("INFO","REGIONAL_SETTINGS",string.Join(" | ",new[]{"LocaleName","Locale","sShortDate","sDate","iDate","sTimeFormat","sTime","iTime","iCalendarType","sDecimal","sList"}.Select(n=>n+"="+key.GetValue(n))));
            } catch(Exception ex) { Exception("REGISTRY_DIAGNOSTIC_UNAVAILABLE",ex); }
            FileSnapshot(Application.ExecutablePath,true);
            HistDataProcesses();
        } catch(Exception ex) { Exception("ENVIRONMENT_FAILED",ex); }
    }
    internal static void HistDataProcesses() {
        var processes=Process.GetProcessesByName("HistData");
        Write("INFO","HISTDATA_PROCESSES","count="+processes.Length);
        foreach(var process in processes) using(process) {
            try {
                Write("INFO","HISTDATA_PROCESS","PID="+process.Id+" SessionId="+process.SessionId+" Start="+process.StartTime.ToString("o")+" Responding="+process.Responding);
                string path=process.MainModule.FileName;
                Write("INFO","HISTDATA_EXECUTABLE","Path="+path+" FileVersion="+FileVersionInfo.GetVersionInfo(path).FileVersion);
            } catch(Exception ex) { Exception("HISTDATA_PROCESS_DETAILS_UNAVAILABLE",ex); }
        }
    }
    internal static void FileSnapshot(string path,bool hash=false) {
        try {
            var info=new FileInfo(path); info.Refresh();
            string detail="Path="+path+" Exists="+info.Exists;
            if(info.Exists) {
                detail+=" Length="+info.Length+" LastWrite="+info.LastWriteTime.ToString("o")+" Attributes="+info.Attributes;
                if(hash) using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)) using(var sha=SHA256.Create()) detail+=" SHA256="+BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");
            }
            Write("DEBUG","FILE",detail);
        } catch(Exception ex) { Exception("FILE_DETAILS_UNAVAILABLE",ex); }
    }
}
}
