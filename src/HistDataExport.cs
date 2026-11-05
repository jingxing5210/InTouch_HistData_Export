using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyVersion("1.8.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.8.0.0")]
[assembly: System.Reflection.AssemblyTitle("InTouch 历史数据导出工具")]
[assembly: System.Reflection.AssemblyDescription("历史数据 CSV 导出；不需要改win操作系统短日期")]

namespace HistDataExport {
internal static class Native {
    internal delegate IntPtr Callback(uint type, uint format, IntPtr conv, IntPtr s1, IntPtr s2, IntPtr data, UIntPtr d1, UIntPtr d2);
    [DllImport("user32.dll", EntryPoint="DdeInitializeW")] internal static extern uint Init(ref uint id, Callback cb, uint flags, uint reserved);
    [DllImport("user32.dll", EntryPoint="DdeCreateStringHandleW", CharSet=CharSet.Unicode)] internal static extern IntPtr Str(uint id, string text, int cp);
    [DllImport("user32.dll", EntryPoint="DdeQueryStringW", CharSet=CharSet.Unicode)] internal static extern uint QueryStr(uint id, IntPtr h, StringBuilder buf, uint len, int cp);
    [DllImport("user32.dll", EntryPoint="DdeFreeStringHandle")] internal static extern bool FreeStr(uint id, IntPtr h);
    [DllImport("user32.dll", EntryPoint="DdeConnect")] internal static extern IntPtr Connect(uint id, IntPtr service, IntPtr topic, IntPtr ctx);
    [DllImport("user32.dll", EntryPoint="DdeDisconnect")] internal static extern bool Disconnect(IntPtr conv);
    [DllImport("user32.dll", EntryPoint="DdeUninitialize")] internal static extern bool Uninit(uint id);
    [DllImport("user32.dll", EntryPoint="DdeGetLastError")] internal static extern uint Error(uint id);
    [DllImport("user32.dll", EntryPoint="DdeClientTransaction")] internal static extern IntPtr Transaction(IntPtr data, uint len, IntPtr conv, IntPtr item, uint format, uint type, uint timeout, out uint result);
    [DllImport("user32.dll", EntryPoint="DdeGetData")] internal static extern uint Data(IntPtr h, byte[] buf, uint len, uint offset);
    [DllImport("user32.dll", EntryPoint="DdeFreeDataHandle")] internal static extern bool FreeData(IntPtr h);
    [DllImport("user32.dll", EntryPoint="DdeCreateDataHandle")] internal static extern IntPtr MakeData(uint id, byte[] data, uint len, uint offset, IntPtr item, uint format, uint flags);
    [DllImport("user32.dll", EntryPoint="DdeNameService")] internal static extern IntPtr NameService(uint id, IntPtr service, IntPtr reserved, uint command);
}

internal interface IHistClient : IDisposable {
    void Poke(string item, string value);
    string Request(string item);
}

internal sealed class DdeClient : IHistClient {
    uint id; IntPtr conv; readonly Native.Callback callback;
    internal static readonly Encoding Ansi = Encoding.GetEncoding(Encoding.Default.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    internal static void RequireProcess(string name) {
        var processes=System.Diagnostics.Process.GetProcessesByName(name);
        bool found=processes.Length>0;
        foreach(var process in processes) process.Dispose();
        if(!found) throw new IOException("未检测到 HistData.exe。请先从 InTouch 安装目录启动 HistData，再点击检查连接。");
    }
    internal DdeClient(string service) {
        var clock=System.Diagnostics.Stopwatch.StartNew();
        SessionLog.Write("DEBUG","DDE_CONNECT_BEGIN","Service="+service+" Topic=topic InitializeFlags=0x10 StringCodePage=1200");
        if(service.Equals("Histdata",StringComparison.OrdinalIgnoreCase)) SessionLog.HistDataProcesses();
        // Avoid slow DDE broadcast discovery when the actual service process is absent.
        if(string.Equals(service,"Histdata",StringComparison.OrdinalIgnoreCase)) RequireProcess("HistData");
        callback = delegate(uint type,uint format,IntPtr conversation,IntPtr s1,IntPtr s2,IntPtr data,UIntPtr d1,UIntPtr d2) {
            SessionLog.Write("DEBUG","DDE_CALLBACK","Type=0x"+type.ToString("X")+" Format="+format+" Conversation=0x"+conversation.ToInt64().ToString("X"));
            return IntPtr.Zero;
        };
        uint code = Native.Init(ref id, callback, 0x10, 0);
        SessionLog.Write("DEBUG","DDE_INITIALIZE","Instance="+id+" Result=0x"+code.ToString("X4"));
        if (code != 0) throw new IOException("DDE 初始化失败，错误码 0x" + code.ToString("X4"));
        IntPtr app=IntPtr.Zero, topic=IntPtr.Zero;
        try {
            app=Native.Str(id, service, 1200); topic=Native.Str(id, "topic", 1200);
            conv=Native.Connect(id, app, topic, IntPtr.Zero);
            if (conv==IntPtr.Zero) throw Failure("连接 HistData", "请先启动 InTouch 的 HistData.exe，并确保两者在同一 Windows 用户会话、使用相同权限等级运行。");
        } catch {
            if(app!=IntPtr.Zero) Native.FreeStr(id,app);
            if(topic!=IntPtr.Zero) Native.FreeStr(id,topic);
            Dispose(); throw;
        }
        if(app!=IntPtr.Zero) Native.FreeStr(id,app);
        if(topic!=IntPtr.Zero) Native.FreeStr(id,topic);
        SessionLog.Write("INFO","DDE_CONNECTED","Conversation=0x"+conv.ToInt64().ToString("X")+" ElapsedMs="+clock.ElapsedMilliseconds);
    }
    IOException Failure(string action, string hint) {
        uint code=Native.Error(id);
        SessionLog.Write("ERROR","DDE_FAILURE","Action="+action+" Code=0x"+code.ToString("X4")+" Instance="+id+" Conversation=0x"+conv.ToInt64().ToString("X")+" Hint="+hint);
        string detail;
        switch(code) {
            case 0x4001: detail="服务忙"; break;
            case 0x4002: detail="读取数据超时"; break;
            case 0x400a: detail="未建立与服务的连接"; break;
            case 0x4006: detail="无效参数"; break;
            case 0x4009: detail="事务未处理（服务未接受本次请求）"; break;
            case 0x4008: detail="内存不足"; break;
            case 0x400b: detail="写入参数超时"; break;
            case 0x400e: detail="服务已退出"; break;
            default: detail="DDE 通信失败"; break;
        }
        var error=new IOException(action+"失败："+detail+"（0x"+code.ToString("X4")+"）。\r\n"+hint);
        error.Data["DdeErrorCode"]=code;
        return error;
    }
    public void Poke(string item, string value) {
        var clock=System.Diagnostics.Stopwatch.StartNew();
        SessionLog.Write("DEBUG","DDE_POKE_BEGIN","Item="+item+" Value="+DebugLogFile.Quote(value)+" Format=CF_TEXT Type=0x4090 TimeoutMs=5000");
        byte[] bytes=Ansi.GetBytes(value+"\0");
        SessionLog.Write("TRACE","DDE_POKE_BYTES","Item="+item+" CodePage="+Ansi.CodePage+" Length="+bytes.Length+" Hex="+BitConverter.ToString(bytes));
        IntPtr h=Native.Str(id,item,1200), data=Marshal.AllocHGlobal(bytes.Length);
        try {
            Marshal.Copy(bytes,0,data,bytes.Length); uint result;
            if(Native.Transaction(data,(uint)bytes.Length,conv,h,1,0x4090,5000,out result)==IntPtr.Zero)
                throw Failure("设置 "+item,"检查参数、HistData 状态和目录权限。原始值："+value);
            SessionLog.Write("DEBUG","DDE_POKE_OK","Item="+item+" Result=0x"+result.ToString("X")+" ElapsedMs="+clock.ElapsedMilliseconds);
        } catch(Exception ex) { SessionLog.Exception("DDE_POKE_EXCEPTION",ex); throw;
        } finally { Marshal.FreeHGlobal(data); Native.FreeStr(id,h); }
    }
    public string Request(string item) {
        var clock=System.Diagnostics.Stopwatch.StartNew();
        SessionLog.Write("TRACE","DDE_REQUEST_BEGIN","Item="+item+" Format=CF_TEXT Type=0x20B0 TimeoutMs=5000");
        IntPtr h=Native.Str(id,item,1200), data=IntPtr.Zero;
        try {
            uint result;
            data=Native.Transaction(IntPtr.Zero,0,conv,h,1,0x20b0,5000,out result);
            if(data==IntPtr.Zero) throw Failure("读取 "+item,"请检查 HistData 是否仍在运行，勿同时使用其他导出工具。");
            uint size=Native.Data(data,null,0,0);
            if(size>1048576) throw new IOException("HistData 返回异常长度的数据："+item);
            byte[] bytes=new byte[size]; uint copied=Native.Data(data,bytes,size,0);
            SessionLog.Write("TRACE","DDE_REQUEST_BYTES","Item="+item+" ExpectedBytes="+size+" CopiedBytes="+copied+" Hex="+BitConverter.ToString(bytes));
            string value=Ansi.GetString(bytes).TrimEnd('\0','\r','\n',' ');
            SessionLog.Write("TRACE","DDE_REQUEST_OK","Item="+item+" Result=0x"+result.ToString("X")+" Bytes="+size+" Value="+DebugLogFile.Quote(value)+" ElapsedMs="+clock.ElapsedMilliseconds);
            return value;
        } catch(Exception ex) { SessionLog.Exception("DDE_REQUEST_EXCEPTION",ex); throw;
        } finally { if(data!=IntPtr.Zero) Native.FreeData(data); Native.FreeStr(id,h); }
    }
    public void Dispose() {
        SessionLog.Write("DEBUG","DDE_DISPOSE","Instance="+id+" Conversation=0x"+conv.ToInt64().ToString("X"));
        if(conv!=IntPtr.Zero) { Native.Disconnect(conv); conv=IntPtr.Zero; }
        if(id!=0) { Native.Uninit(id); id=0; }
        GC.KeepAlive(callback);
    }
}

internal sealed class ExportOptions {
    internal string DataDir, DbDir, Output, Tags, Duration, Interval;
    internal DateTime Start;
    internal string WireDateFormat="Windows";
    internal bool TagsNormalized;
    internal int TimeoutSeconds=300;
    internal List<KeyValuePair<string,string>> Parameters(string staging) {
        return new List<KeyValuePair<string,string>> {
            Pair("DataDir",DataDir), Pair("DBDir",DbDir),
            Pair("StartDate",DateProtocol.Format(Start,WireDateFormat)),
            Pair("StartTime",Start.ToString("HH:mm:ss",CultureInfo.InvariantCulture)),
            Pair("Duration",Duration), Pair("Interval",Interval),
            Pair("FileName",staging), Pair("Tags",Tags), Pair("PrintTagNames","1")
        };
    }
    static KeyValuePair<string,string> Pair(string k,string v) { return new KeyValuePair<string,string>(k,v); }
}

internal static class Exporter {
    internal static string NormalizeTags(string raw,bool checkInputLength=true) {
        raw=raw??"";
        if(checkInputLength) TagInputRules.ValidateLength(raw.Length,"目标标记名");
        string[] tags=Regex.Split(raw.Trim(),@"[,，;；\s]+").Where(x=>x.Length>0).ToArray();
        if(tags.Length==0) throw new ArgumentException("请填写要导出的 InTouch 标记名，或导入标签文本文件。");
        var all=new List<string>{"$Date","$Time"};
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"$Date","$Time"};
        foreach(string tag in tags) {
            if(tag.IndexOfAny(new[]{'"','\0'})>=0) throw new ArgumentException("标记名含有非法字符："+tag);
            if(seen.Add(tag)) all.Add(tag);
        }
        if(all.Count==2) throw new ArgumentException("除日期和时间外，至少需要一个数据标记。");
        return string.Join(",",all);
    }
    internal static double Seconds(string value) {
        Match m=Regex.Match(value,@"^([0-9]+(?:\.[0-9]+)?)([smhdw])$");
        if(!m.Success) throw new ArgumentException("无效的持续时间或间隔："+value);
        double n=double.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture);
        int mult=new Dictionary<string,int>{{"s",1},{"m",60},{"h",3600},{"d",86400},{"w",604800}}[m.Groups[2].Value];
        return n*mult;
    }
    internal static void Validate(ExportOptions o) {
        if(!Directory.Exists(o.DataDir)) throw new DirectoryNotFoundException("历史数据目录不存在："+o.DataDir);
        if(!Directory.Exists(o.DbDir)) throw new DirectoryNotFoundException("InTouch 项目目录不存在："+o.DbDir);
        if(!o.TagsNormalized) TagInputRules.ValidateLength((o.Tags??"").Length,"目标标记名");
        o.Tags=NormalizeTags(o.Tags,false); o.TagsNormalized=true;
        ExportFiles.Batches(o); // Reject an individually oversized tag before communicating with HistData.
        if(o.WireDateFormat!="MM/dd/yy" && o.WireDateFormat!="yyyy/MM/dd" && o.WireDateFormat!="Windows") throw new ArgumentException("不支持的 HistData 日期协议格式。");
        double duration=Seconds(o.Duration), interval=Seconds(o.Interval);
        if(duration<=0 || interval<=0 || duration>3628800 || interval>duration)
            throw new ArgumentException("持续时间须大于零且不超过 42 天；采样间隔须大于零且不超过持续时间。");
        o.Start.AddSeconds(duration);
        if(o.WireDateFormat=="MM/dd/yy" && (o.Start.Year<2000 || o.Start.AddSeconds(duration).Year>=2100))
            throw new ArgumentException("两位年份兼容模式仅允许 2000—2099 年。其他年份请先验证 HistData 是否支持四位年份协议。");
        o.Output=Path.GetFullPath(o.Output.Trim());
        if(!string.Equals(Path.GetExtension(o.Output),".csv",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("输出文件扩展名必须为 .csv。");
        if(Path.GetFileName(o.Output).IndexOfAny(Path.GetInvalidFileNameChars())>=0 || Regex.IsMatch(Path.GetFileNameWithoutExtension(o.Output),@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",RegexOptions.IgnoreCase))
            throw new ArgumentException("文件名包含非法字符或 Windows 保留名称。");
        if(!Directory.Exists(Path.GetDirectoryName(o.Output))) throw new DirectoryNotFoundException("输出目录不存在，请重新选择："+Path.GetDirectoryName(o.Output));
        foreach(var p in o.Parameters(o.Output)) DdeClient.Ansi.GetBytes(p.Value);
        string probe=Path.Combine(Path.GetDirectoryName(o.Output),".histdata_writecheck_"+Guid.NewGuid().ToString("N"));
        using(new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {}
        File.Delete(probe);
    }
    internal static string AvailablePath(string requested, DateTime stamp, int attempt) {
        if(attempt==0 && !File.Exists(requested) && !Directory.Exists(requested)) return requested;
        string suffix="_"+stamp.ToString("yyyyMMdd_HHmmss",CultureInfo.InvariantCulture)+(attempt>1?"_"+(attempt-1).ToString("D3"):"");
        return Path.Combine(Path.GetDirectoryName(requested),Path.GetFileNameWithoutExtension(requested)+suffix+".csv");
    }
    internal static string Publish(string staging,string requested) {
        DateTime stamp=DateTime.Now;
        for(int attempt=0;attempt<10000;attempt++) {
            string dest=AvailablePath(requested,stamp,attempt);
            if(File.Exists(dest)||Directory.Exists(dest)) { SessionLog.Write("DEBUG","PUBLISH_COLLISION","Attempt="+attempt+" Destination="+dest); continue; }
            try { File.Move(staging,dest); SessionLog.Write("INFO","FILE_PUBLISHED","Source="+staging+" Destination="+dest); SessionLog.FileSnapshot(dest,true); return dest; }
            catch(IOException) { if(File.Exists(dest)||Directory.Exists(dest)) continue; throw; }
        }
        throw new IOException("同名文件过多，无法生成可用文件名。请更换输出文件名称。");
    }
    internal static string Run(ExportOptions o, Func<IHistClient> connect, Action<string> report, Func<bool> stop, Action<ExportProgress> progress=null) {
        Validate(o);
        SessionLog.Write("INFO","EXPORT_VALIDATED","Start="+o.Start.ToString("o")+" WireDateMode="+o.WireDateFormat+" Duration="+o.Duration+" Interval="+o.Interval+" TimeoutSeconds="+o.TimeoutSeconds+" Output="+o.Output+" DataDir="+o.DataDir+" DbDir="+o.DbDir+" Tags="+DebugLogFile.Quote(o.Tags));
        var batches=ExportFiles.Batches(o);
        string prefix=Path.Combine(Path.GetDirectoryName(o.Output),".histdata_"+Guid.NewGuid().ToString("N"));
        string staging=prefix+".csv";
        var parts=new List<string>();
        long blankValues=0,rows=0;
        int completed=0, total=o.Tags.Split(',').Length-2;
        if(progress!=null) progress(new ExportProgress(0,total,false));
        try {
            using(IHistClient client=connect()) {
                if(client.Request("WriteFile")!="0") throw new IOException("HistData 正在导出数据，请等待当前任务完成。勿同时使用旧程序或其他 HistData 客户端。");
                for(int batch=0;batch<batches.Count;batch++) {
                    if(stop()) throw new OperationCanceledException("已在提交下一批数据前停止。");
                    string part=batches.Count==1?staging:prefix+"_part"+(batch+1).ToString("D3")+".csv";
                    parts.Add(part);
                    SessionLog.Write("INFO","BATCH_BEGIN","Batch="+(batch+1)+" Total="+batches.Count+" Staging="+part+" Tags="+batches[batch].Tags);
                    foreach(var p in batches[batch].Parameters(part)) DdeClient.Ansi.GetBytes(p.Value);
                    report("正在读取第 "+(batch+1)+" / "+batches.Count+" 批标签数据。");
                    CsvStatistics statistics=RunBatch(batches[batch],client,part,report,stop);
                    blankValues+=statistics.BlankValues; rows=statistics.Rows;
                    completed+=batches[batch].Tags.Split(',').Length-2;
                    SessionLog.Write("INFO","BATCH_END","CompletedTags="+completed+" TotalTags="+total);
                    if(progress!=null) progress(new ExportProgress(completed,total,false));
                }
            }
            if(stop()) throw new OperationCanceledException("已停止，未生成正式结果。");
            if(parts.Count>1) {
                report("各批数据已读取，正在核对时间轴并合并 CSV……");
                ExportFiles.Merge(parts,batches,staging,stop);
            }
            if(stop()) throw new OperationCanceledException("已停止，未发布正式文件。");
            string dest=Publish(staging,o.Output);
            if(progress!=null) progress(new ExportProgress(total,total,true));
            foreach(string part in parts) {
                if(part==staging) continue;
                try { File.Delete(part); } catch(Exception ex) { report("正式结果已保存；中间文件清理失败："+part+"（"+ex.Message+"）"); }
            }
            if(blankValues>0) {
                SessionLog.Write("WARN","EXPORT_CONTAINS_BLANK_VALUES","Rows="+rows+" BlankValues="+blankValues+" TotalTags="+total);
                report("导出完成（含 "+blankValues.ToString("N0")+" 个空值，请核对时间范围或历史记录）："+dest);
            } else report("导出完成："+dest);
            return dest;
        } catch(Exception ex) {
            if(parts.Count>0) ex.Data["Recovery"]="本次中间文件路径：\r\n"+string.Join("\r\n",parts.Concat(new[]{staging}).Distinct())+"\r\n中间文件可能尚未完成，不作为正式结果。已完成的标签："+completed+" / "+total+"。";
            SessionLog.Exception("EXPORT_FAILED",ex);
            foreach(string part in parts) SessionLog.FileSnapshot(part);
            SessionLog.FileSnapshot(staging);
            throw;
        }
    }
    static CsvStatistics RunBatch(ExportOptions o,IHistClient client,string staging,Action<string> report,Func<bool> stop) {
        try {
                foreach(var p in o.Parameters(staging)) {
                    if(stop()) throw new OperationCanceledException("已在提交导出前停止。");
                    report("设置 "+p.Key); client.Poke(p.Key,p.Value);
                }
                ExportFiles.ReadBackParameters(client,o,staging);
                if(stop()) throw new OperationCanceledException("已在提交导出前停止。");
                report("正在提交导出请求……");
                // A timeout during this call may still mean the server accepted the request.
                client.Poke("WriteFile","1");
                report("已提交导出，等待 HistData 完成……");
                var watch=System.Diagnostics.Stopwatch.StartNew();
                while(true) {
                    if(stop()) throw new OperationCanceledException("已停止等待。HistData 可能继续导出，请等待其结束后再启动新任务。");
                    string busy=client.Request("WriteFile");
                    SessionLog.FileSnapshot(staging);
                    if(busy=="0") {
                        string status=client.Request("Status"), error=client.Request("Error");
                        if(status!="1") throw new IOException("HistData 导出失败："+error+"\r\n请核对日期协议、历史文件覆盖的时间段、标记名以及项目目录。");
                        if(!string.Equals(error,"None",StringComparison.OrdinalIgnoreCase) && error!="无" && error.Length>0)
                            report("HistData 返回信息："+error);
                        if(!File.Exists(staging)) throw new IOException("HistData 已结束，但没有生成输出文件。请检查输出目录和 HistData 的运行权限。");
                        using(var file=new FileStream(staging,FileMode.Open,FileAccess.Read,FileShare.None)) {
                            if(file.Length==0) throw new IOException("HistData 生成了空文件。请检查时间范围与历史数据。");
                        }
                        return ExportFiles.ValidatePart(staging,o,stop);
                    }
                    if(busy!="1") throw new IOException("HistData 返回了无法识别的 WriteFile 状态："+busy);
                    if(watch.Elapsed.TotalSeconds>=o.TimeoutSeconds) throw new TimeoutException("等待 HistData 超时（"+o.TimeoutSeconds+" 秒）。任务可能仍在运行，请先检查 HistData 状态。可缩短导出范围或增加等待时间。");
                    report("HistData 正在导出，已等待 "+(int)watch.Elapsed.TotalSeconds+" 秒……");
                    Thread.Sleep(300);
                }
        } catch(Exception ex) {
            if(!(ex is OperationCanceledException)) ex.Data["HistDataDiagnostics"]=ExportFiles.Diagnostics(client,o,staging);
            throw;
        }
    }
}

internal sealed class MainForm : Form {
    TextBox dataDir=new TextBox(), dbDir=new TextBox(), output=new TextBox();
    TagTextBox tags=new TagTextBox();
    DateTimePicker date=new DateTimePicker();
    ComboBox hour=new ComboBox(), minute=new ComboBox(), second=new ComboBox();
    NumericUpDown duration=new NumericUpDown(), wait=new NumericUpDown();
    ComboBox unit=new ComboBox(), interval=new ComboBox(), protocol=new ComboBox();
    Button run=new Button(), check=new Button(), stop=new Button(), open=new Button(),openLogs=new Button(),copyLogs=new Button();
    Label status=new Label(), estimate=new Label();
    Label tagCapacity=new Label { Name="TagCapacity",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true };
    TextBox log=new TextBox(); GreenProgress progress=new GreenProgress();
    Panel settings=new Panel(); volatile bool stopping; bool running; string lastOutput;
    readonly bool rememberInputs;
    readonly string inputPath;
    readonly System.Windows.Forms.Timer inputSaveTimer=new System.Windows.Forms.Timer { Interval=700 };
    string lastSaveError;
    string displayedLogError;

    internal MainForm(bool remember=true,string savedInputsPath=null) {
        rememberInputs=remember;
        inputPath=savedInputsPath??InputStore.DefaultPath;
        dataDir.Name="DataDir"; dbDir.Name="DbDir"; output.Name="Output"; tags.Name="Tags";
        Text="InTouch 历史数据导出 · v1.8 正式版"; Size=new Size(920,850); MinimumSize=new Size(820,750);
        StartPosition=FormStartPosition.CenterScreen; Font=new Font("Microsoft YaHei UI",9F); AutoScaleMode=AutoScaleMode.Dpi;
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Padding=new Padding(18)};
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,430));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,32)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
        Controls.Add(layout);
        layout.Controls.Add(new Label{Text="从 InTouch 历史记录导出 CSV",Font=new Font(Font.FontFamily,16,FontStyle.Bold),AutoSize=true,Margin=new Padding(0,0,0,15)},0,0);
        settings.Dock=DockStyle.Fill; layout.Controls.Add(settings,0,1);
        var grid=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=10};
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,135)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));
        settings.Controls.Add(grid);
        AddRow(grid,0,"历史数据目录",dataDir,BrowseFolder(dataDir));
        AddRow(grid,1,"InTouch 项目目录",dbDir,BrowseFolder(dbDir));
        date.Format=DateTimePickerFormat.Custom; date.CustomFormat="yyyy/MM/dd"; date.Value=DateTime.Today;
        var start=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false}; date.Width=155; start.Controls.Add(date);
        FillNumbers(hour,24); FillNumbers(minute,60); FillNumbers(second,60);
        foreach(var c in new[]{hour,minute,second}) { c.Width=58; start.Controls.Add(c); }
        start.Controls.Add(new Label{Text="时 / 分 / 秒",AutoSize=true,Padding=new Padding(4,5,0,0)});
        AddRow(grid,2,"开始日期 / 时间",start,null);
        duration.Minimum=1; duration.Maximum=42; duration.Value=1; duration.Width=85;
        unit.DropDownStyle=ComboBoxStyle.DropDownList; unit.Items.AddRange(new object[]{"天","小时","分钟"}); unit.SelectedIndex=0; unit.Width=80;
        var span=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false}; span.Controls.Add(duration); span.Controls.Add(unit);
        span.Controls.Add(new Label{Text="最长 42 天",AutoSize=true,Padding=new Padding(10,5,0,0)}); AddRow(grid,3,"持续时间",span,null);
        interval.DropDownStyle=ComboBoxStyle.DropDownList; interval.Items.AddRange(new object[]{"1 秒","5 秒","10 秒","15 秒","30 秒","1 分钟","2 分钟","5 分钟","10 分钟","15 分钟","30 分钟","1 小时","2 小时","6 小时","12 小时","1 天"}); interval.SelectedIndex=5;
        AddRow(grid,4,"采样间隔",interval,null);
        AddRow(grid,5,"输出 CSV",output,Button("选择…",delegate { using(var d=new SaveFileDialog{Filter="CSV 文件 (*.csv)|*.csv",FileName="历史数据.csv",OverwritePrompt=false}) if(d.ShowDialog(this)==DialogResult.OK) output.Text=d.FileName; }));
        tags.Multiline=true; tags.MaxLength=TagInputRules.MaxCharacters; tags.ScrollBars=ScrollBars.Vertical; tags.AcceptsReturn=true;
        tags.PasteRejected+=delegate(long length) { ShowError(TagInputRules.TooLong(length,"粘贴后的目标标记名")); };
        var tagPanel=new TableLayoutPanel { Dock=DockStyle.Fill,RowCount=2,ColumnCount=1,Margin=new Padding(0) };
        tagPanel.RowStyles.Add(new RowStyle(SizeType.Percent,100)); tagPanel.RowStyles.Add(new RowStyle(SizeType.Absolute,25));
        tags.Dock=DockStyle.Fill; tags.Margin=new Padding(0); tagPanel.Controls.Add(tags,0,0); tagPanel.Controls.Add(tagCapacity,0,1);
        AddRow(grid,6,"目标标记名",tagPanel,Button("导入文本",ImportTags)); grid.RowStyles[6].Height=100;
        protocol.Name="DateProtocol"; protocol.DropDownStyle=ComboBoxStyle.DropDownList; protocol.Items.AddRange(new object[]{"手动协议：MM/dd/yy","手动协议：yyyy/MM/dd","自动适配（不需要改win操作系统短日期）"}); protocol.SelectedIndex=2;
        AddRow(grid,7,"HistData 日期协议",protocol,null);
        wait.Minimum=30; wait.Maximum=86400; wait.Value=300; wait.Increment=30;
        var timeout=new FlowLayoutPanel{Dock=DockStyle.Fill}; wait.Width=100; timeout.Controls.Add(wait); timeout.Controls.Add(new Label{Text="秒；不需要改win操作系统短日期",AutoSize=true,Padding=new Padding(5,5,0,0)});
        AddRow(grid,8,"单批最长等待",timeout,null);
        estimate.AutoSize=true; estimate.ForeColor=Color.FromArgb(80,90,110); grid.Controls.Add(estimate,1,9); grid.SetColumnSpan(estimate,2);
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false}; layout.Controls.Add(actions,0,2);
        run.Text="开始导出"; run.Width=130; run.Height=34; run.Click+=StartExport; actions.Controls.Add(run);
        check.Text="检查连接"; check.Width=110; check.Height=34; check.Click+=CheckConnection; actions.Controls.Add(check);
        stop.Text="停止等待"; stop.Width=110; stop.Height=34; stop.Enabled=false; stop.Click+=delegate { SessionLog.Write("INFO","STOP_CLICKED","用户请求停止等待，未发送 WriteFile=0。"); stopping=true; stop.Enabled=false; }; actions.Controls.Add(stop);
        open.Text="打开输出目录"; open.Width=135; open.Height=34; open.Enabled=false; open.Click+=delegate { if(lastOutput!=null) System.Diagnostics.Process.Start("explorer.exe","/select,\""+lastOutput+"\""); }; actions.Controls.Add(open);
        openLogs.Text="打开日志目录"; openLogs.Width=105; openLogs.Height=34; openLogs.Enabled=SessionLog.PathName!=null;
        openLogs.Click+=delegate { try { System.Diagnostics.Process.Start("explorer.exe","/select,\""+SessionLog.PathName+"\""); } catch(Exception ex) { ShowError(ex); } }; actions.Controls.Add(openLogs);
        copyLogs.Text="复制完整日志"; copyLogs.Width=115; copyLogs.Height=34; copyLogs.Enabled=SessionLog.PathName!=null;
        copyLogs.Click+=delegate { try { Clipboard.SetText(SessionLog.ReadSnapshot()); Log("已复制当前完整日志，可粘贴到文本文件或聊天中。"); } catch(Exception ex) { ShowError(ex); } }; actions.Controls.Add(copyLogs);
        status.Dock=DockStyle.Fill; status.AutoEllipsis=true; status.Text="请启动 HistData.exe，然后填写目录和目标标记名。"; layout.Controls.Add(status,0,3);
        log.Multiline=true; log.ReadOnly=true; log.ScrollBars=ScrollBars.Both; log.WordWrap=false; log.Dock=DockStyle.Fill; log.BackColor=Color.FromArgb(245,247,250); layout.Controls.Add(log,0,4);
        progress.Dock=DockStyle.Fill; progress.Margin=new Padding(3,10,3,0); layout.Controls.Add(progress,0,5);
        duration.ValueChanged+=delegate { Estimate(); }; unit.SelectedIndexChanged+=delegate { duration.Maximum=unit.SelectedIndex==0?42:unit.SelectedIndex==1?1008:60480; Estimate(); };
        interval.SelectedIndexChanged+=delegate { Estimate(); }; tags.TextChanged+=delegate { Estimate(); };
        date.ValueChanged+=delegate { Estimate(); };
        foreach(var time in new[]{hour,minute,second}) time.SelectedIndexChanged+=delegate { Estimate(); };
        output.Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"历史数据.csv");
        if(rememberInputs) {
            string warning;
            var previous=InputStore.Load(inputPath,out warning);
            if(previous!=null) { dataDir.Text=previous.DataDir; dbDir.Text=previous.DbDir; output.Text=previous.Output; tags.Text=previous.Tags; }
            if(warning!=null) Log(warning);
            inputSaveTimer.Tick+=delegate { inputSaveTimer.Stop(); SaveInputs(); };
            foreach(var input in new[]{dataDir,dbDir,output,tags}) input.TextChanged+=delegate { inputSaveTimer.Stop(); inputSaveTimer.Start(); };
        }
        FormClosing+=delegate(object sender,FormClosingEventArgs e) {
            if(running) { e.Cancel=true; MessageBox.Show(this,"任务尚未结束，请先点击“停止等待”，等待操作返回后再关闭。停止等待不会终止 HistData 的后台写入。","任务正在运行"); }
            else { inputSaveTimer.Stop(); SaveInputs(); }
        };
        FormClosed+=delegate { inputSaveTimer.Dispose(); };
        if(SessionLog.LastError!=null) Log(SessionLog.LastError);
        Estimate();
    }
    void SaveInputs() {
        if(!rememberInputs) return;
        try {
            InputStore.Save(inputPath,new RememberedInputs { DataDir=dataDir.Text,DbDir=dbDir.Text,Output=output.Text,Tags=tags.Text });
            lastSaveError=null;
        } catch(Exception ex) {
            SessionLog.Exception("INPUTS_SAVE_FAILED",ex);
            if(ex.Message!=lastSaveError) Log("无法记住本次输入，下次启动可能无法恢复；当前仍可使用。原因："+ex.Message);
            lastSaveError=ex.Message;
        }
    }
    static Button Button(string text,EventHandler click) { var b=new Button{Text=text,Dock=DockStyle.Fill}; b.Click+=click; return b; }
    Button BrowseFolder(TextBox target) { return Button("浏览…",delegate { using(var d=new FolderBrowserDialog{Description="选择目录",ShowNewFolderButton=false}) if(d.ShowDialog(this)==DialogResult.OK) target.Text=d.SelectedPath; }); }
    void AddRow(TableLayoutPanel g,int row,string title,Control value,Control action) {
        g.RowStyles.Add(new RowStyle(SizeType.Absolute,row==9?28:36));
        g.Controls.Add(new Label{Text=title,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,row);
        value.Dock=DockStyle.Fill; value.Margin=new Padding(3,4,8,4); g.Controls.Add(value,1,row);
        if(action!=null) { action.Margin=new Padding(3,3,0,3); action.Dock=DockStyle.Top; action.Height=28; g.Controls.Add(action,2,row); } else g.SetColumnSpan(value,2);
    }
    static void FillNumbers(ComboBox c,int count) { c.DropDownStyle=ComboBoxStyle.DropDownList; for(int i=0;i<count;i++) c.Items.Add(i.ToString("D2")); c.SelectedIndex=0; }
    string SpanValue() { return duration.Value.ToString(CultureInfo.InvariantCulture)+new[]{"d","h","m"}[unit.SelectedIndex]; }
    string IntervalValue() { return new[]{"1s","5s","10s","15s","30s","1m","2m","5m","10m","15m","30m","1h","2h","6h","12h","1d"}[interval.SelectedIndex]; }
    void Estimate() {
        tagCapacity.Text="已输入 "+tags.TextLength.ToString("N0")+" / "+TagInputRules.MaxCharacters.ToString("N0")+" 个字符（含分隔符、换行）";
        tagCapacity.ForeColor=tags.TextLength>=TagInputRules.MaxCharacters?Color.DarkOrange:Color.FromArgb(80,90,110);
        try {
            long rows=(long)Math.Floor(Exporter.Seconds(SpanValue())/Exporter.Seconds(IntervalValue()))+1;
            int count=Exporter.NormalizeTags(tags.Text).Split(',').Length-2;
            DateTime end=date.Value.Date.AddHours(hour.SelectedIndex).AddMinutes(minute.SelectedIndex).AddSeconds(second.SelectedIndex).AddSeconds(Exporter.Seconds(SpanValue()));
            estimate.Text="预计 "+rows.ToString("N0")+" 个采样时刻，"+count+" 个标签"+(end>DateTime.Now?"；含未来时间，可能有空值。":"（含结束时刻，实际以 HistData 为准）。");
        } catch { estimate.Text="标签可用逗号、分号或换行分隔；自动添加 $Date、$Time，按输入顺序导出。"; }
    }
    void ImportTags(object sender,EventArgs e) {
        using(var d=new OpenFileDialog{Filter="标签文本 / 原 INI (*.txt;*.csv;*.ini)|*.txt;*.csv;*.ini|所有文件 (*.*)|*.*"}) {
            if(d.ShowDialog(this)!=DialogResult.OK) return;
            try { LoadTagFile(d.FileName); } catch(Exception ex) { ShowError(ex); }
        }
    }
    internal void LoadTagFile(string path) {
        string text=TagTextImport.ReadFile(path);
        Exporter.NormalizeTags(text); // Validate first; never replace the existing list on failed import.
        tags.Text=text;
        Log("已导入标签："+path+"（"+text.Length.ToString("N0")+" 个字符）");
    }
    ExportOptions Options() {
        return new ExportOptions { DataDir=dataDir.Text.Trim(), DbDir=dbDir.Text.Trim(), Output=output.Text,
            Start=date.Value.Date.AddHours(hour.SelectedIndex).AddMinutes(minute.SelectedIndex).AddSeconds(second.SelectedIndex),
            Duration=SpanValue(), Interval=IntervalValue(), Tags=tags.Text,
            WireDateFormat=new[]{"MM/dd/yy","yyyy/MM/dd","Windows"}[protocol.SelectedIndex], TimeoutSeconds=(int)wait.Value };
    }
    void Log(string message) {
        SessionLog.Write("INFO","UI_MESSAGE",message);
        if(InvokeRequired) { BeginInvoke(new Action<string>(DisplayLog),message); return; }
        DisplayLog(message);
    }
    void DisplayLog(string message) {
        status.Text=message.Split(new[]{'\r','\n'})[0];
        // Every polling event is in the file; only the screen suppresses repeated status lines.
        if(!message.StartsWith("HistData 正在导出")) log.AppendText(DateTime.Now.ToString("HH:mm:ss")+"  "+message+Environment.NewLine);
        if(SessionLog.LastError!=null && SessionLog.LastError!=displayedLogError) {
            displayedLogError=SessionLog.LastError;
            log.AppendText(DateTime.Now.ToString("HH:mm:ss")+"  注意："+displayedLogError+"；本次文件日志可能不完整。"+Environment.NewLine);
        }
    }
    void UpdateProgress(ExportProgress value) {
        SessionLog.Write("INFO","PROGRESS","Percent="+value.Percent+" CompletedTags="+value.CompletedTags+" TotalTags="+value.TotalTags+" Published="+value.Published);
        if(InvokeRequired) { BeginInvoke(new Action<ExportProgress>(DisplayProgress),value); return; }
        DisplayProgress(value);
    }
    void DisplayProgress(ExportProgress value) {
        progress.Set(value);
    }
    internal void PreviewProgress(int percent) { progress.Set(new ExportProgress(Math.Max(0,Math.Min(100,percent)),100,percent>=100)); }
    void SetBusy(bool busy) { running=busy; settings.Enabled=!busy; run.Enabled=!busy; check.Enabled=!busy; stop.Enabled=busy; }
    void Work(Action action) {
        stopping=false; SetBusy(true);
        string operation=SessionLog.Operation;
        var t=new Thread(delegate() {
            SessionLog.Operation=operation;
            var clock=System.Diagnostics.Stopwatch.StartNew(); string outcome="success";
            try { action(); }
            catch(Exception ex) { outcome=ex is OperationCanceledException?"stopped":"failed"; SessionLog.Exception("WORKER_EXCEPTION",ex); BeginInvoke(new Action<Exception>(ShowError),ex); }
            finally {
                SessionLog.Write("INFO","OPERATION_END","Outcome="+outcome+" ElapsedMs="+clock.ElapsedMilliseconds);
                BeginInvoke(new Action(delegate { SetBusy(false); open.Enabled=lastOutput!=null; if(SessionLog.Operation==operation) SessionLog.Operation=null; }));
            }
        }); t.SetApartmentState(ApartmentState.STA); t.IsBackground=true; t.Start();
    }
    void CheckConnection(object sender,EventArgs e) {
        SessionLog.BeginOperation("connection_check");
        Log("正在连接 HistData……");
        Work(delegate { using(var client=new DdeClient("Histdata")) Log("连接成功。HistData WriteFile="+client.Request("WriteFile")+"；Status="+client.Request("Status")+"；Error="+client.Request("Error")); });
    }
    void StartExport(object sender,EventArgs e) {
        SessionLog.BeginOperation("export");
        SessionLog.Write("INFO","RAW_INPUTS","DataDir="+DebugLogFile.Quote(dataDir.Text)+" DbDir="+DebugLogFile.Quote(dbDir.Text)+" Output="+DebugLogFile.Quote(output.Text)+" Tags="+DebugLogFile.Quote(tags.Text)+" Date="+date.Value.ToString("o")+" Time="+hour.Text+":"+minute.Text+":"+second.Text+" Duration="+SpanValue()+" Interval="+IntervalValue()+" Protocol="+protocol.Text+" Timeout="+wait.Value);
        inputSaveTimer.Stop(); SaveInputs();
        progress.Reset();
        ExportOptions o;
        try { o=Options(); Exporter.Validate(o); }
        catch(Exception ex) { SessionLog.Write("INFO","OPERATION_END","Outcome=validation_failed"); ShowError(ex); SessionLog.Operation=null; return; }
        Log("开始时间："+o.Start.ToString("yyyy/MM/dd HH:mm:ss")+"；持续时间："+o.Duration+"；间隔："+o.Interval);
        Log("已有同名文件时保留原文件，自动追加 yyyyMMdd_HHmmss；"+(lastSaveError==null?"已记住目录、输出路径和目标标记名。":"输入记忆失败，原因见日志。"));
        Log("Windows 当前短日期格式："+DateProtocol.WindowsPattern+"；本次发送 StartDate="+DateProtocol.Format(o.Start,o.WireDateFormat));
        if(o.Start.AddSeconds(Exporter.Seconds(o.Duration))>DateTime.Now) {
            Log("提示：请求范围包含尚未发生的时间，部分数据可能为空。按原范围导出，不裁剪、不将空值填为 0。");
            SessionLog.Write("WARN","FUTURE_TIME_RANGE","Start="+o.Start.ToString("o")+" RequestedEnd="+o.Start.AddSeconds(Exporter.Seconds(o.Duration)).ToString("o"));
        }
        Log("进度按已校验完成的标签批次推进；全部数据核对并保存成功后达到 100%。");
        Work(delegate {
            using(var mutex=new Mutex(false,"Local\\InTouchHistDataExport_Exclusive")) {
                bool held=false;
                try {
                    try { held=mutex.WaitOne(0); } catch(AbandonedMutexException) { held=true; }
                    if(!held) throw new IOException("另一个新版导出任务正在运行，请等待完成。");
                    lastOutput=Exporter.Run(o,delegate { Log("正在连接 HistData……"); return new DdeClient("Histdata"); },Log,delegate { return stopping; },UpdateProgress);
                } finally { if(held) mutex.ReleaseMutex(); }
            }
        });
    }
    void ShowError(Exception ex) {
        SessionLog.Exception("ERROR_DISPLAY",ex);
        string text=ex.Message;
        if(ex is UnauthorizedAccessException) text="没有文件或目录的写入权限。请更换输出目录，并检查 HistData 的运行账户。\r\n"+text;
        if(ex is EncoderFallbackException) text="目录或标签包含当前 Windows ANSI 代码页无法表示的字符。请改用兼容的目录和标记名。\r\n"+text;
        if(ex.Data.Contains("HistDataDiagnostics")) text+="\r\n\r\n"+ex.Data["HistDataDiagnostics"];
        if(ex.Data.Contains("Recovery")) text+="\r\n\r\n"+ex.Data["Recovery"];
        if(ex.Data.Contains("MergeRecovery")) text+="\r\n\r\n"+ex.Data["MergeRecovery"];
        Log((ex is OperationCanceledException?"已停止：":"失败：")+text);
        string popup=text;
        if(ex.Data.Contains("HistDataDiagnostics")) {
            string diagnostic=Convert.ToString(ex.Data["HistDataDiagnostics"]);
            var lines=diagnostic.Split(new[]{"\r\n"},StringSplitOptions.None);
            popup=ex.Message+"\r\n\r\n"+string.Join("\r\n",lines.Take(4).Concat(lines.Where(l=>l.StartsWith("StartDate =") || l.StartsWith("Duration =") || l.StartsWith("Interval =") || l.StartsWith("Windows 短日期格式 ="))));
            string histError=lines.FirstOrDefault(l=>l.StartsWith("Error = "))??"";
            if(histError.IndexOf("STARTDATE",StringComparison.OrdinalIgnoreCase)>=0 || histError.IndexOf("START DATE",StringComparison.OrdinalIgnoreCase)>=0)
                popup+="\r\n\r\n请使用默认的自动适配模式，不需要改win操作系统短日期。若仍失败，请提供本次完整日志以便继续分析。";
            popup+="\r\n\r\n完整诊断、实际参数和中间文件路径见窗口下方日志，可选中复制。";
        }
        if(popup.Length>1800) popup=popup.Substring(0,1800)+"\r\n……完整信息见窗口日志。";
        if(SessionLog.PathName!=null) popup+="\r\n\r\n完整运行日志已自动保存，可点击“打开日志目录”获取。";
        MessageBox.Show(this,popup,ex is OperationCanceledException?"已停止等待":"导出工具提示",MessageBoxButtons.OK,ex is OperationCanceledException?MessageBoxIcon.Information:MessageBoxIcon.Warning);
    }
}

internal static class Program {
    [STAThread] static void Main(string[] args) {
        if(args.Length>0 && args[0]=="--self-test") {
            string report=args.Length>1?args[1]:"selftest.txt";
            SessionLog.Start(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report)),"test-logs"));
            try { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); SessionLog.EnvironmentSnapshot(); Environment.ExitCode=SelfTests.Run(report); } finally { SessionLog.End(); } return;
        }
        if(args.Length>1 && args[0]=="--preview") {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using(var form=new MainForm(false)) { form.ShowInTaskbar=false; form.Opacity=0; form.Show(); if(args.Length>2) form.PreviewProgress(int.Parse(args[2],CultureInfo.InvariantCulture)); form.PerformLayout(); Application.DoEvents(); using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height)); bitmap.Save(args[1]); } form.Close(); } return;
        }
        SessionLog.Start(SessionLog.DefaultDirectory);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException+=delegate(object sender,ThreadExceptionEventArgs e) { SessionLog.Exception("UNHANDLED_UI_EXCEPTION",e.Exception); MessageBox.Show("程序发生未处理异常："+e.Exception.Message+"\r\n日志："+SessionLog.PathName,"运行日志",MessageBoxButtons.OK,MessageBoxIcon.Error); };
        AppDomain.CurrentDomain.UnhandledException+=delegate(object sender,UnhandledExceptionEventArgs e) { var ex=e.ExceptionObject as Exception; if(ex!=null) SessionLog.Exception("UNHANDLED_PROCESS_EXCEPTION",ex); else SessionLog.Write("FATAL","UNHANDLED_PROCESS_EXCEPTION",Convert.ToString(e.ExceptionObject)); SessionLog.End(); };
        AppDomain.CurrentDomain.ProcessExit+=delegate { SessionLog.End(); };
        try {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            SessionLog.EnvironmentSnapshot();
            if(SessionLog.LastError!=null) MessageBox.Show(SessionLog.LastError,"运行日志无法写入",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            Application.Run(new MainForm());
        } catch(Exception ex) { Environment.ExitCode=1; SessionLog.Exception("FATAL_MAIN",ex); MessageBox.Show(ex.Message+"\r\n日志："+SessionLog.PathName,"程序启动失败"); }
        finally { SessionLog.Write("INFO","APPLICATION_EXIT","ExitCode="+Environment.ExitCode); SessionLog.End(); }
    }
}
}
