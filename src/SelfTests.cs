using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace HistDataExport {
// An isolated DDE service; it never registers as the real Histdata service.
internal sealed class TestServer : IDisposable {
    readonly Thread thread; readonly ManualResetEvent ready=new ManualResetEvent(false);
    readonly Native.Callback callback;
    uint id; IntPtr service; ApplicationContext context; Control dispatcher; Exception startupError;
    internal readonly string Name="HistDataExport_TEST_"+Guid.NewGuid().ToString("N");
    internal readonly Dictionary<string,string> Values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    internal readonly List<string> Writes=new List<string>();
    internal bool FailExport, KeepBusy, MissingFile, RejectWrite, RejectDiagnostics, HeaderOnly, MismatchTime,BlankLastRow;
    internal int ExportCount,FailOnExport;
    internal readonly List<string> ReadItems=new List<string>();
    internal bool RejectReadBack;
    internal int MaxTagBytes;
    internal TestServer() {
        Values["WriteFile"]="0"; Values["Status"]="1"; Values["Error"]="None";
        callback=Handle;
        thread=new Thread(delegate() {
            try {
                uint code=Native.Init(ref id,callback,0,0);
                if(code!=0) throw new Exception("Test DDE init: "+code);
                service=Native.Str(id,Name,1200);
                if(Native.NameService(id,service,IntPtr.Zero,1)==IntPtr.Zero) throw new Exception("Test service registration failed");
                context=new ApplicationContext(); dispatcher=new Control(); IntPtr window=dispatcher.Handle;
                ready.Set(); Application.Run(context);
            } catch(Exception ex) { startupError=ex; ready.Set(); }
            finally {
                if(service!=IntPtr.Zero) { Native.NameService(id,service,IntPtr.Zero,2); Native.FreeStr(id,service); }
                if(id!=0) Native.Uninit(id);
                if(dispatcher!=null) dispatcher.Dispose();
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground=true; thread.Start();
        if(!ready.WaitOne(5000)) throw new Exception("Test service startup timed out");
        if(startupError!=null) throw startupError;
    }
    string Text(IntPtr s) { var b=new StringBuilder(1024); Native.QueryStr(id,s,b,1024,1200); return b.ToString(); }
    IntPtr Handle(uint type,uint format,IntPtr conv,IntPtr s1,IntPtr s2,IntPtr data,UIntPtr d1,UIntPtr d2) {
        try {
            if(type==0x1062) return Text(s1)=="topic" ? new IntPtr(1) : IntPtr.Zero;
            if(type==0x4090 && format==1) {
                uint size=Native.Data(data,null,0,0); byte[] bytes=new byte[size]; Native.Data(data,bytes,size,0);
                string item=Text(s2), value=DdeClient.Ansi.GetString(bytes).TrimEnd('\0');
                if(item=="Tags" && MaxTagBytes>0 && size>MaxTagBytes) return IntPtr.Zero;
                if(item=="WriteFile" && value=="1" && RejectWrite) {
                    Values["Error"]="Invalid STARTDATE item"; Values["Status"]="0"; Values["WriteFile"]="0";
                    return IntPtr.Zero;
                }
                Values[item]=value; Writes.Add(item);
                if(item=="WriteFile" && value=="1") {
                    ExportCount++;
                    if(FailExport || (FailOnExport>0 && ExportCount==FailOnExport)) { Values["Status"]="0"; Values["Error"]="Test: invalid historical tag"; Values["WriteFile"]="0"; }
                    else if(!KeepBusy) {
                        if(!MissingFile) {
                            string[] tags=Values["Tags"].Split(',').Skip(2).ToArray();
                            string values=string.Join(",",tags.Select(t=>t=="TEST_TAG"?"123.5":t=="A"?"11":t=="B"?"22":"33"));
                            string content="Date,Time,"+string.Join(",",tags)+"\r\n";
                            if(!HeaderOnly) content+="10/15/26,00:00:00,"+values+"\r\n10/15/26,"+(MismatchTime && ExportCount==2?"00:02:00":"00:01:00")+","+(BlankLastRow?string.Join(",",tags.Select(t=>"")):values)+"\r\n";
                            File.WriteAllText(Values["FileName"],content,Encoding.ASCII);
                        }
                        Values["Status"]="1"; Values["Error"]="None"; Values["WriteFile"]="0";
                    }
                }
                return new IntPtr(0x8000);
            }
            if(type==0x20b0 && format==1) {
                ReadItems.Add(Text(s2));
                if(RejectReadBack && Text(s2)=="StartDate") return IntPtr.Zero;
                if(RejectDiagnostics && Text(s2)=="Error") return IntPtr.Zero;
                string value; if(!Values.TryGetValue(Text(s2),out value)) return IntPtr.Zero;
                byte[] bytes=DdeClient.Ansi.GetBytes(value+"\0");
                return Native.MakeData(id,bytes,(uint)bytes.Length,0,s2,1,0);
            }
        } catch { return IntPtr.Zero; }
        return IntPtr.Zero;
    }
    public void Dispose() {
        if(context!=null && dispatcher!=null) dispatcher.BeginInvoke(new Action(context.ExitThread));
        if(!thread.Join(5000)) throw new Exception("Test server did not exit");
        ready.Dispose(); GC.KeepAlive(callback);
    }
}

internal static class SelfTests {
    static readonly List<string> results=new List<string>();
    static int failed;
    static string reportPath;
    static void Check(bool ok,string name) { results.Add((ok?"PASS ":"FAIL ")+name); if(!ok) failed++; File.WriteAllLines(reportPath,results,Encoding.UTF8); }
    static void Expect<T>(Action a,string name) where T:Exception {
        try { a(); Check(false,name+" (no exception)"); }
        catch(T) { Check(true,name); }
    }
    static ExportOptions Options(string root) {
        return new ExportOptions{DataDir=root,DbDir=root,Output=Path.Combine(root,"result.csv"),Tags="TEST_TAG",Duration="1d",Interval="1m",Start=new DateTime(2026,10,15),TimeoutSeconds=1,WireDateFormat="MM/dd/yy"};
    }
    internal static int Run(string report) {
        reportPath=report;
        string root=Path.Combine(Path.GetTempPath(),"HistDataExportTests_"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            LoggingTests(root);
            TagInputTests(root);
            Check(Exporter.NormalizeTags("A，B\r\nA;$Time")=="$Date,$Time,A,B","Custom tags normalize and deduplicate without fixed process tags");
            Expect<ArgumentException>(delegate { Exporter.NormalizeTags("$Date,$Time"); },"Reject empty measurement list");
            var options=Options(root);
            Check(new ExportOptions().WireDateFormat=="Windows","Default protocol adapts to existing Windows format instead of requiring system changes");
            string unchangedPattern=DateProtocol.WindowsPattern;
            Check(DateProtocol.FormatWithWindowsPattern(new DateTime(2026,11,4),"yyy-MM-dd")=="2026-11-04","Customer log date pattern yyy-MM-dd produces 2026-11-04 without changing Windows settings");
            Check(DateProtocol.FormatWithWindowsPattern(new DateTime(2026,11,4),"MM/dd/yy")=="11/04/26" && DateProtocol.FormatWithWindowsPattern(new DateTime(2026,11,4),"yyyy/MM/dd")=="2026/11/04" && DateProtocol.WindowsPattern==unchangedPattern,"Explicit native pattern formatting preserves month/day and leaves Windows short date unchanged");
            Check(options.Parameters("test.csv").First(x=>x.Key=="StartDate").Value=="10/15/26","Legacy wire date is independent of UI format");
            options.WireDateFormat="yyyy/MM/dd";
            Check(options.Parameters("test.csv").First(x=>x.Key=="StartDate").Value=="2026/10/15","Four digit wire date option");
            options.WireDateFormat="Windows";
            Check(options.Parameters("test.csv").First(x=>x.Key=="StartDate").Value==DateProtocol.Format(options.Start,"Windows") && DateProtocol.WindowsPattern.Length>0,"Windows date protocol uses native current-user short date without changing settings");
            PersistenceTests(root);
            options=Options(root); options.Duration="43d";
            Expect<ArgumentException>(delegate { Exporter.Validate(options); },"Reject duration above HistData 42 day limit");
            options=Options(root); options.Interval="2d";
            Expect<ArgumentException>(delegate { Exporter.Validate(options); },"Reject interval above duration");
            options=Options(root); options.Start=new DateTime(2100,1,1);
            Expect<ArgumentException>(delegate { Exporter.Validate(options); },"Reject ambiguous two digit year");
            options=Options(root); options.Output=Path.Combine(root,"CON.csv");
            Expect<ArgumentException>(delegate { Exporter.Validate(options); },"Reject Windows reserved filename");
            options=Options(root); options.DataDir=Path.Combine(root,"missing");
            Expect<DirectoryNotFoundException>(delegate { Exporter.Validate(options); },"Reject missing history directory");
            using(var server=new TestServer()) {
                options=Options(root);
                string dest=Exporter.Run(options,delegate { return new DdeClient(server.Name); },delegate {},delegate { return false; });
                Check(File.ReadAllText(dest).Contains("123.5"),"Real DDE transport: write parameters, trigger export, poll status, publish CSV");
                Check(server.Values["StartDate"]=="10/15/26" && server.Values["Tags"]=="$Date,$Time,TEST_TAG","Real DDE transport receives correct date and custom tags");
                options.WireDateFormat="Windows";
                Exporter.Run(options,delegate { return new DdeClient(server.Name); },delegate {},delegate { return false; });
                Check(server.Values["StartDate"]==DateProtocol.Format(options.Start,"Windows"),"Real DDE transport supports Windows short date compatibility mode");
                Check(server.ReadItems.Contains("StartDate") && server.ReadItems.Contains("StartTime") && server.ReadItems.Contains("FileName"),"Parameters are read back from server before export trigger");
                server.RejectReadBack=true;
                Exporter.Run(options,delegate { return new DdeClient(server.Name); },delegate {},delegate { return false; });
                Check(File.Exists(options.Output),"Unsupported read-back is logged without preventing valid export or forcing retry");
                server.RejectReadBack=false;
                options.WireDateFormat="MM/dd/yy";
                Check(server.Writes.Last()=="WriteFile" && server.Writes.IndexOf("FileName")<server.Writes.IndexOf("WriteFile"),"Export only triggered after parameters");
                using(var locked=new FileStream(dest,FileMode.Open,FileAccess.Read,FileShare.None)) {
                    string renamed=Exporter.Run(options,delegate { return new DdeClient(server.Name); },delegate {},delegate { return false; });
                    Check(renamed!=dest && File.Exists(renamed),"Locked existing filename: save with timestamp and preserve original");
                }
                string tmp=Path.Combine(root,"temp.csv"); File.WriteAllText(tmp,"new");
                DateTime stamp=DateTime.Now;
                string collision=Exporter.AvailablePath(dest,stamp,1); File.WriteAllText(collision,"existing suffix");
                string next=Exporter.Publish(tmp,dest);
                Check(next!=dest && next!=collision && File.ReadAllText(collision)=="existing suffix","Timestamp collision: append sequence; never overwrite");
                server.Values["WriteFile"]="1";
                Expect<IOException>(delegate { Exporter.Run(options,delegate { return new DdeClient(server.Name); },delegate {},delegate { return false; }); },"Reject already busy HistData");
                server.Values["WriteFile"]="0"; server.FailExport=true;
                Expect<IOException>(delegate { Exporter.Run(options,delegate { return new DdeClient(server.Name); },delegate {},delegate { return false; }); },"Surface HistData error status");
                server.FailExport=false; server.MissingFile=true;
                Expect<IOException>(delegate { Exporter.Run(options,delegate { return new DdeClient(server.Name); },delegate {},delegate { return false; }); },"Do not report success when output is missing");
                server.MissingFile=false; server.KeepBusy=true;
                Expect<TimeoutException>(delegate { Exporter.Run(options,delegate { return new DdeClient(server.Name); },delegate {},delegate { return false; }); },"Timeout leaves existing file intact");
                server.Values["WriteFile"]="0";
                Expect<OperationCanceledException>(delegate { Exporter.Run(options,delegate { return new DdeClient(server.Name); },delegate {},delegate { return true; }); },"Stop before export submission");
                server.Values["WriteFile"]="0"; bool submitted=false;
                Expect<OperationCanceledException>(delegate { Exporter.Run(options,delegate { return new DdeClient(server.Name); },delegate(string message) { if(message.StartsWith("已提交导出")) submitted=true; },delegate { return submitted; }); },"Stop waiting after submission without sending WriteFile=0");
                Check(server.Values["WriteFile"]=="1","Stopping wait does not cancel server or falsely mark completion");
                Check(File.ReadAllText(dest).Contains("123.5"),"Original output preserved across failures");
                server.Values["WriteFile"]="0"; server.KeepBusy=false;
                EnhancedTests(root,server);
                LargeTagTests(root,server);
                Check(true,"Test service cleanup begins");
            }
            Check(true,"Test service cleanup completed");
            Expect<IOException>(delegate { DdeClient.RequireProcess("NoSuchHistData_"+Guid.NewGuid().ToString("N")); },"Missing HistData process produces diagnostic before DDE broadcast discovery");
        } catch(Exception ex) { failed++; results.Add("FAIL Unexpected: "+ex); }
        finally {
            try { Directory.Delete(root,true); } catch(Exception ex) { results.Add("Test temp cleanup: "+ex.Message); }
            results.Add("Failures: "+failed); File.WriteAllLines(report,results,Encoding.UTF8);
        }
        return failed==0?0:1;
    }
    static void PersistenceTests(string root) {
        string path=Path.Combine(root,"remember","inputs.xml"),warning;
        Check(InputStore.Load(path,out warning)==null && warning==null,"First launch with no saved inputs uses defaults");
        var inputs=new RememberedInputs { DataDir=@"E:\历史数据 & 测试",DbDir=@"D:\项目 <一>",Output=@"D:\报表\历史数据.csv",Tags="A\r\nB,中文标记\r\n" };
        InputStore.Save(path,inputs);
        var loaded=InputStore.Load(path,out warning);
        Check(loaded.DataDir==inputs.DataDir && loaded.DbDir==inputs.DbDir && loaded.Output==inputs.Output && loaded.Tags==inputs.Tags && warning==null,"Saved paths and multiline tags round-trip Chinese, spaces and XML characters exactly");
        string xml=File.ReadAllText(path);
        Check(!xml.Contains("StartDate") && !xml.Contains("Interval") && !xml.Contains("WireDateFormat"),"Only four requested input fields are persisted");
        InputStore.Save(path,new RememberedInputs { Tags="new" });
        File.WriteAllText(path,"<broken>"); loaded=InputStore.Load(path,out warning);
        Check(loaded.Tags==inputs.Tags && warning.Contains("备用文件"),"Corrupt primary settings recover previous atomic backup");
        File.WriteAllText(path+".bak","<broken>");
        Check(InputStore.Load(path,out warning)==null && warning!=null,"Unrecoverable settings corruption permits fresh input without crash");
        InputStore.Save(path,new RememberedInputs()); loaded=InputStore.Load(path,out warning);
        Check(loaded.Tags=="" && loaded.Output=="","Deliberately cleared fields remain cleared on next load");
        File.WriteAllText(path,"<!DOCTYPE LastInputs [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><LastInputs><Tags>&x;</Tags></LastInputs>");
        File.WriteAllText(path+".bak","<broken>");
        Check(InputStore.Load(path,out warning)==null && warning!=null,"Settings parser rejects DTD and external entities");
        string uiPath=Path.Combine(root,"ui-inputs.xml");
        using(var form=new MainForm(true,uiPath)) {
            form.ShowInTaskbar=false; form.Opacity=0; form.Show();
            ((TextBox)form.Controls.Find("DataDir",true).Single()).Text=inputs.DataDir;
            ((TextBox)form.Controls.Find("DbDir",true).Single()).Text=inputs.DbDir;
            ((TextBox)form.Controls.Find("Output",true).Single()).Text=inputs.Output;
            ((TextBox)form.Controls.Find("Tags",true).Single()).Text=inputs.Tags;
            form.Close();
        }
        using(var form=new MainForm(true,uiPath)) {
            Check(((TextBox)form.Controls.Find("DataDir",true).Single()).Text==inputs.DataDir && ((TextBox)form.Controls.Find("DbDir",true).Single()).Text==inputs.DbDir && ((TextBox)form.Controls.Find("Output",true).Single()).Text==inputs.Output && ((TextBox)form.Controls.Find("Tags",true).Single()).Text==inputs.Tags,"Closing and reopening real form restores all four fields, even before debounce timer fires");
            Check(((ComboBox)form.Controls.Find("DateProtocol",true).Single()).SelectedIndex==2,"UI starts in automatic date adaptation mode while keeping four remembered input fields");
        }
        string previewPath=Path.Combine(root,"preview-no-settings.xml");
        using(var form=new MainForm(false,previewPath)) { form.ShowInTaskbar=false; form.Opacity=0; form.Show(); form.Close(); }
        Check(!File.Exists(previewPath),"Preview mode does not write user input settings");
    }
    static void LoggingTests(string root) {
        string blocked=Path.Combine(root,"not-a-directory"); File.WriteAllText(blocked,"occupied");
        string path;
        using(var trace=new DebugLogFile(Path.Combine(root,"logger-fixed"))) {
            path=trace.PathName;
            Check(path!=null && path.StartsWith(Path.Combine(root,"logger-fixed")) && trace.LastError==null,"Logger creates its file only in the explicitly specified directory");
            trace.Write("INFO","ESCAPE_TEST","中文\r\nsecond line\t\"quoted\"\\path\0","operation-test",7);
            string snapshot=trace.ReadSnapshot();
            Check(snapshot.Contains("中文\\r\\nsecond line\\t\\\"quoted\\\"\\\\path\\u0000") && snapshot.Contains("operation-test"),"UTF-8 JSONL preserves complete multiline text, quotes, tabs, paths and operation IDs");
            Check(snapshot.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries).Length==2,"Logs are flushed and readable while writer is active");
            var workers=new List<Thread>();
            for(int i=0;i<4;i++) {
                int worker=i;
                var thread=new Thread(delegate() { for(int j=0;j<30;j++) trace.Write("DEBUG","CONCURRENT","worker="+worker+" record="+j,"parallel",Thread.CurrentThread.ManagedThreadId); });
                workers.Add(thread); thread.Start();
            }
            foreach(var thread in workers) thread.Join();
            string[] lines=trace.ReadSnapshot().Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries);
            Check(lines.Length==122 && lines.Select((l,i)=>l.StartsWith("{\"seq\":"+(i+1)+",")).All(v=>v),"Concurrent log writes retain every event with ordered sequence numbers");
        }
        Check(File.ReadAllLines(path).Last().Contains("SESSION_END"),"Normal logger shutdown appends footer without truncating earlier events");
        using(var trace=new DebugLogFile(blocked)) Check(trace.PathName==null && trace.LastError!=null && trace.LastError.Contains(blocked),"Unwritable log directory reports its path without switching to another directory");
        string workingDirectory=Environment.CurrentDirectory;
        try {
            Environment.CurrentDirectory=root;
            Check(SessionLog.DefaultDirectory==Path.Combine(Application.StartupPath,"logs"),"Default logs directory follows executable location rather than shortcut working directory");
        } finally { Environment.CurrentDirectory=workingDirectory; }
        using(var form=new MainForm(false)) {
            var screenLog=form.Controls.Cast<Control>().SelectMany(c=>FindTextBoxes(c)).Single(t=>t.Multiline && t.ReadOnly);
            Check(!screenLog.Text.Contains("完整运行日志自动保存于"),"Initial UI does not append the automatic log-path announcement");
        }
        var timestamp=new DateTime(2026,11,5,12,34,56,789);
        using(var first=new DebugLogFile(timestamp,Path.Combine(root,"log-names")))
        using(var second=new DebugLogFile(timestamp,Path.Combine(root,"log-names"))) {
            Check(Path.GetFileName(first.PathName)=="操作日志-20261105_123456_789.jsonl" && Path.GetFileName(second.PathName)=="操作日志-20261105_123456_789_001.jsonl","Operation log uses timestamp filename and collision suffix without overwriting earlier log");
        }
        try { throw new IOException("outer trace",new InvalidOperationException("inner trace")); }
        catch(Exception ex) { ex.Data["DdeErrorCode"]=0x4009u; ex.Data["HistDataDiagnostics"]="START DATE 项目无效"; SessionLog.Exception("LOGGER_EXCEPTION_TEST",ex); }
        string current=SessionLog.ReadSnapshot();
        Check(current.Contains("outer trace") && current.Contains("inner trace") && current.Contains("DdeErrorCode") && current.Contains("START DATE 项目无效") && current.Contains("SelfTests.cs"),"Exception trace retains inner exception, custom diagnostics, error code and source line symbols");
    }
    static IEnumerable<TextBox> FindTextBoxes(Control root) {
        var box=root as TextBox; if(box!=null) yield return box;
        foreach(Control child in root.Controls) foreach(var found in FindTextBoxes(child)) yield return found;
    }
    static void LargeTagTests(string root,TestServer server) {
        server.KeepBusy=false; server.FailExport=false; server.RejectWrite=false; server.RejectReadBack=false;
        server.RejectDiagnostics=false; server.FailOnExport=0; server.MismatchTime=false; server.HeaderOnly=false; server.BlankLastRow=false;
        server.Values["WriteFile"]="0"; server.MaxTagBytes=ExportFiles.TagRequestByteLimit;
        foreach(int count in new[]{100,1000}) {
            var o=Options(root);
            string[] tags=Enumerable.Range(0,count).Select(i=>"LongPlant_Area_Equipment_"+i.ToString("D4")+"_Measurement").ToArray();
            o.Tags=Exporter.NormalizeTags(string.Join(",",tags)); o.Output=Path.Combine(root,"tags_"+count+".csv");
            var batches=ExportFiles.Batches(o);
            Check(batches.All(b=>DdeClient.Ansi.GetByteCount(b.Tags+"\0")<=ExportFiles.TagRequestByteLimit) && batches.SelectMany(b=>b.Tags.Split(',').Skip(2)).SequenceEqual(tags),count+" tags split by encoded byte length without loss or reordering");
            var progress=new List<ExportProgress>();
            string result=Exporter.Run(o,delegate { return new DdeClient(server.Name); },delegate {},delegate { return false; },progress.Add);
            using(var csv=new CsvInput(result)) {
                var header=csv.Read(); var row=csv.Read(); var last=csv.Read();
                Check(header.Count==count+2 && header.Skip(2).SequenceEqual(tags) && row.Count==count+2 && last.Count==count+2 && csv.Read()==null && progress.Last().Percent==100,"Native DDE simulation exports and merges "+count+" long tag names to one complete CSV");
            }
            if(count==1000) Check(batches.Count>ExportFiles.MergeFanIn && !Directory.GetFiles(root,"*.merge_*.csv").Any(),"More than 32 batches use hierarchical merge and clean successful intermediate files");
        }
        server.MaxTagBytes=0;
        using(var form=new MainForm(false)) Check(((TextBox)form.Controls.Find("Tags",true).Single()).MaxLength>=1048576,"Tag input capacity exceeds the previous default 32K character limit");
        var oversized=Options(root); oversized.Tags="$Date,$Time,"+new string('A',ExportFiles.TagRequestByteLimit);
        Expect<ArgumentException>(delegate { ExportFiles.Batches(oversized); },"Individually oversized tag is rejected rather than truncated or silently omitted");
    }
    static void TagInputTests(string root) {
        int limit=TagInputRules.MaxCharacters;
        string exact=Path.Combine(root,"tags-exact.txt"),over=Path.Combine(root,"tags-over.txt");
        string maximum="A"+new string(' ',limit-1);
        File.WriteAllText(exact,maximum,new UTF8Encoding(true));
        File.WriteAllText(over,maximum+new string(' ',123),new UTF8Encoding(false));
        Check(TagTextImport.ReadFile(exact).Length==limit,"Import accepts exactly 1,048,576 characters and excludes UTF-8 BOM from count");
        try { TagTextImport.ReadFile(over); Check(false,"Oversized import reports counts"); }
        catch(ArgumentException ex) {
            Check((long)ex.Data["ActualCharacters"]==limit+123L && (long)ex.Data["ExceededCharacters"]==123 && ex.Message.Contains("1,048,699") && ex.Message.Contains("1,048,576"),"Oversized import reports exact actual, limit and exceeded character counts");
        }
        string gbk=Path.Combine(root,"tags-gbk.txt"); File.WriteAllText(gbk,"A,中文标记",Encoding.GetEncoding(936));
        Check(TagTextImport.ReadFile(gbk)=="A,中文标记","GBK import counts decoded characters rather than source bytes");
        string utf16=Path.Combine(root,"tags-utf16.txt"); File.WriteAllText(utf16,"A\r\nB",Encoding.Unicode);
        Check(TagTextImport.ReadFile(utf16)=="A\r\nB","BOM-detected Unicode import preserves CRLF character count");
        string ini=Path.Combine(root,"tags-long-metadata.ini");
        File.WriteAllText(ini,"[Parameters]\r\nother="+new string('Z',limit+300)+"\r\ntxtTags=A,B\r\n",Encoding.UTF8);
        Check(TagTextImport.ReadFile(ini)=="A,B","INI limit applies to txtTags value rather than unrelated file metadata");
        File.WriteAllText(ini,"txtTags="+maximum+"12345\r\n",Encoding.UTF8);
        try { TagTextImport.ReadFile(ini); Check(false,"Oversized INI value rejected"); }
        catch(ArgumentException ex) { Check((long)ex.Data["ActualCharacters"]==limit+5L,"Oversized INI tag value reports its own exact character count"); }
        using(var form=new MainForm(false)) {
            var input=(TextBox)form.Controls.Find("Tags",true).Single();
            var capacity=(Label)form.Controls.Find("TagCapacity",true).Single();
            input.Text="A,B";
            Check(input.MaxLength==limit && capacity.Text.Contains("3 / "+limit.ToString("N0")),"UI displays live character count and enforces the same manual input limit");
            Expect<ArgumentException>(delegate { form.LoadTagFile(over); },"UI import rejects oversized file before assignment");
            Check(input.Text=="A,B","Failed oversized import preserves existing tag list");
            form.LoadTagFile(exact);
            Check(input.TextLength==limit && capacity.Text.Contains(limit.ToString("N0")+" / "+limit.ToString("N0")),"Boundary-sized import is not truncated and updates UI counter");
        }
        using(var input=new TagTextBox()) {
            input.Text="A,B"; long rejected=0; input.PasteRejected+=delegate(long n) { rejected=n; };
            Check(!input.AcceptPaste(maximum) && rejected==limit+3L && input.Text=="A,B","Oversized paste is rejected as a whole without truncating or changing text");
            input.SelectAll();
            Check(input.AcceptPaste(maximum),"Paste limit accounts for selected text being replaced");
        }
        var options=Options(root); options.Tags=maximum;
        Exporter.Validate(options); Exporter.Validate(options);
        Check(options.Tags=="$Date,$Time,A" && options.TagsNormalized,"Boundary input stays valid across repeated export validation and automatic date columns");
    }
    static void EnhancedTests(string root,TestServer server) {
        var options=Options(root); options.Tags="A,B,C,TEST_TAG"; options.Output=Path.Combine(root,"merged.csv");
        var progress=new List<ExportProgress>(); var logs=new List<string>(); server.ExportCount=0;
        string dest=Exporter.Run(options,delegate { return new DdeClient(server.Name); },logs.Add,delegate { return false; },progress.Add);
        Check(progress.Select(p=>p.Percent).SequenceEqual(new[]{0,25,50,75,99,100}),"Progress follows completed tag batches; 100 reserved for published output");
        Check(progress.Last().Published && File.Exists(dest),"100 percent only after final output exists");
        Check(File.ReadAllLines(dest).SequenceEqual(new[]{"Date,Time,A,B,C,TEST_TAG","10/15/26,00:00:00,11,22,33,123.5","10/15/26,00:01:00,11,22,33,123.5"}),"Batch merge preserves tag order, timestamps, row count and original values");
        Check(logs.IndexOf("正在提交导出请求……")<logs.IndexOf("已提交导出，等待 HistData 完成……"),"Submitted message only follows accepted request");
        server.BlankLastRow=true;
        var blankOptions=Options(root); blankOptions.Tags="A,B"; blankOptions.Output=Path.Combine(root,"blank-summary.csv");
        var blankLogs=new List<string>();
        string blankOutput=Exporter.Run(blankOptions,delegate { return new DdeClient(server.Name); },blankLogs.Add,delegate { return false; });
        using(var csv=new CsvInput(blankOutput)) {
            csv.Read(); var first=csv.Read(); var last=csv.Read();
            Check(first[2]=="11" && first[3]=="22" && last[2]=="" && last[3]=="" && blankLogs.Any(l=>l.Contains("含 2 个空值")),"Successful multi-batch export reports aggregate blank values without filling or discarding records");
        }
        server.BlankLastRow=false;
        server.RejectWrite=true; logs.Clear(); progress.Clear();
        try {
            Exporter.Run(options,delegate { return new DdeClient(server.Name); },logs.Add,delegate { return false; },progress.Add);
            Check(false,"Rejected WriteFile captures diagnostic");
        } catch(IOException ex) {
            string diagnostic=Convert.ToString(ex.Data["HistDataDiagnostics"]);
            Check(ex.Data.Contains("DdeErrorCode") && (uint)ex.Data["DdeErrorCode"]==0x4009,"Original DDE 0x4009 retained before diagnostic reads");
            Check(diagnostic.Contains("Error = Invalid STARTDATE item") && diagnostic.Contains("Status = 0") && diagnostic.Contains("WriteFile = 0") && diagnostic.Contains("StartDate = 10/15/26"),"Rejected WriteFile includes server Error, Status, WriteFile and exact parameters");
        }
        Check(!logs.Any(m=>m.StartsWith("已提交导出")) && progress.All(p=>p.Percent==0),"Rejected submission never claims acceptance or advances green progress");
        server.RejectDiagnostics=true;
        try {
            Exporter.Run(options,delegate { return new DdeClient(server.Name); },delegate {},delegate { return false; });
            Check(false,"Diagnostic fallback");
        } catch(IOException ex) {
            string diagnostic=Convert.ToString(ex.Data["HistDataDiagnostics"]);
            Check(ex.Message.StartsWith("设置 WriteFile") && diagnostic.Contains("Error = 无法读取") && diagnostic.Contains("Status = 0"),"Unreadable Error does not mask original exception or prevent other diagnostic reads");
        }
        server.RejectWrite=false; server.RejectDiagnostics=false; server.ExportCount=0; server.FailOnExport=2; progress.Clear();
        Expect<IOException>(delegate { Exporter.Run(options,delegate { return new DdeClient(server.Name); },delegate {},delegate { return false; },progress.Add); },"Failure in second batch stops whole export");
        Check(progress.Select(p=>p.Percent).SequenceEqual(new[]{0,25}) && File.ReadAllText(dest).Contains("123.5"),"Failure preserves actual completed progress and original output");
        server.FailOnExport=0; server.ExportCount=0; server.HeaderOnly=true; progress.Clear();
        Expect<IOException>(delegate { Exporter.Run(options,delegate { return new DdeClient(server.Name); },delegate {},delegate { return false; },progress.Add); },"Header-only output rejected before counting completed data");
        Check(progress.All(p=>p.Percent==0),"Empty batch does not advance progress");
        server.HeaderOnly=false; server.ExportCount=0; server.MismatchTime=true; options.Output=Path.Combine(root,"must_not_publish.csv"); progress.Clear();
        Expect<IOException>(delegate { Exporter.Run(options,delegate { return new DdeClient(server.Name); },delegate {},delegate { return false; },progress.Add); },"Mismatched batch timestamps reject unsafe merge");
        Check(!File.Exists(options.Output) && progress.All(p=>p.Percent<100),"Failed merge neither publishes data nor shows 100 percent");
        server.MismatchTime=false; server.ExportCount=0; progress.Clear(); bool stop=false;
        Expect<OperationCanceledException>(delegate { Exporter.Run(options,delegate { return new DdeClient(server.Name); },delegate {},delegate { return stop; },delegate(ExportProgress p) { progress.Add(p); if(p.CompletedTags==1) stop=true; }); },"Stop between batches prevents next server submission");
        Check(server.ExportCount==1 && progress.Last().Percent==25,"Stopping between batches retains confirmed progress only");
        server.ExportCount=0; server.KeepBusy=true; progress.Clear(); options.Tags="A";
        Expect<TimeoutException>(delegate { Exporter.Run(options,delegate { return new DdeClient(server.Name); },delegate {},delegate { return false; },progress.Add); },"Waiting on busy single batch times out");
        Check(progress.All(p=>p.Percent==0),"Elapsed time does not fabricate data progress");
        server.KeepBusy=false; server.Values["WriteFile"]="0";
        progress.Clear();
        Exporter.Run(options,delegate { return new DdeClient(server.Name); },delegate {},delegate { return false; },progress.Add);
        Check(progress.Select(p=>p.Percent).SequenceEqual(new[]{0,99,100}),"Single tag shows confirmed completion only; no invented intermediate percentages");
        var many=Options(root); many.Tags="$Date,$Time,"+string.Join(",",Enumerable.Range(0,65).Select(i=>"Tag"+i));
        var batches=ExportFiles.Batches(many);
        Check(batches.Count<=32 && string.Join(",",batches.SelectMany(b=>b.Tags.Split(',').Skip(2)))==string.Join(",",many.Tags.Split(',').Skip(2)),"Large tag selection batches are bounded and preserve order");
        CsvTests(root);
        using(var bar=new GreenProgress { Size=new Size(240,32) }) {
            bar.Set(new ExportProgress(1,4,false));
            using(var bitmap=new Bitmap(240,32)) {
                bar.DrawToBitmap(bitmap,new Rectangle(0,0,240,32));
                Check(bitmap.GetPixel(5,3).ToArgb()==Color.FromArgb(40,167,69).ToArgb() && bitmap.GetPixel(220,3).ToArgb()!=Color.FromArgb(40,167,69).ToArgb(),"Green progress fills completed portion from left to right");
            }
            Check(bar.Percent==25,"UI percent matches confirmed export data");
        }
    }
    static void CsvTests(string root) {
        string first=Path.Combine(root,"quoted1.csv"), second=Path.Combine(root,"quoted2.csv"), merged=Path.Combine(root,"quoted_merge.csv");
        File.WriteAllText(first,"Date,Time,A\r\n2026/11/04,00:00:00,\"1,25\"\r\n2026/11/04,00:01:00,\"a\"\"b\"\r\n",new UTF8Encoding(true));
        File.WriteAllText(second,"Date,Time,B\r\n2026/11/04,00:00:00,\r\n2026/11/04,00:01:00,\"中文\r\n多行\"\r\n",new UTF8Encoding(true));
        var a=Options(root); a.Tags="$Date,$Time,A"; var b=Options(root); b.Tags="$Date,$Time,B";
        var statistics=ExportFiles.ValidatePart(second,b,delegate { return false; });
        Check(statistics.Rows==2 && statistics.BlankValues==1 && statistics.LastNonEmptyTime=="2026/11/04 00:01:00","CSV summary counts blank values while preserving quoted and multiline values");
        ExportFiles.Merge(new[]{first,second},new[]{a,b},merged,delegate { return false; });
        using(var csv=new CsvInput(merged)) {
            csv.Read(); var row1=csv.Read(); var row2=csv.Read();
            Check(row1[2]=="1,25" && row1[3]=="" && row2[2]=="a\"b" && row2[3]=="中文\r\n多行" && csv.Read()==null,"Streaming CSV merge preserves quoted commas, quotes, empty values, Chinese and multiline fields");
        }
        File.WriteAllText(second,"Date,Time,B\r\n2026/11/04,00:00:00,1\r\n",Encoding.ASCII);
        Expect<IOException>(delegate { ExportFiles.Merge(new[]{first,second},new[]{a,b},Path.Combine(root,"bad_rows.csv"),delegate { return false; }); },"Unequal batch row counts reject merge");
        File.WriteAllText(second,"Date,Time,WRONG\r\n2026/11/04,00:00:00,1\r\n",Encoding.ASCII);
        Expect<IOException>(delegate { ExportFiles.ValidatePart(second,b,delegate { return false; }); },"Unexpected tag header rejected");
        File.WriteAllText(second,"Date,Time,B\r\n\r\n2026/11/04,00:00:00,1\r\n\r\n",Encoding.ASCII);
        ExportFiles.ValidatePart(second,b,delegate { return false; });
        Check(true,"Blank CSV lines do not invalidate complete records");
        File.WriteAllText(second,"Date,Time,B\r\n2026/11/04,00:00:00,0\r\n2026/11/04,00:01:00,\r\n",Encoding.ASCII);
        byte[] before=File.ReadAllBytes(second);
        statistics=ExportFiles.ValidatePart(second,b,delegate { return false; });
        Check(statistics.BlankValues==1 && statistics.LastNonEmptyTime=="2026/11/04 00:00:00" && before.SequenceEqual(File.ReadAllBytes(second)),"Blank value reporting does not classify zero as missing or modify source CSV");
        Check(ExportFiles.EquivalentParameter("FileName",@"D:\LWZ\report.csv",@"D:\LWZ\REPORT.CSV") && ExportFiles.EquivalentParameter("Duration","1d","1D") && ExportFiles.EquivalentParameter("Tags","$Date,$Time,A","\"$DATE,$TIME,A\""),"HistData capitalization and quoted Tags are recognized as equivalent read-back values");
        Check(!ExportFiles.EquivalentParameter("Tags","$Date,$Time,A","\"$DATE,$TIME,B\"") && !ExportFiles.EquivalentParameter("StartDate","2026-11-05","11/05/26"),"Read-back equivalence does not hide changed tags or ambiguous date text");
    }
}
}
