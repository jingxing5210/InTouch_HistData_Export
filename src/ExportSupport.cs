using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace HistDataExport {
internal sealed class CsvStatistics {
    internal long Rows,BlankValues;
    internal string LastNonEmptyTime;
}
internal sealed class ExportProgress {
    internal readonly int CompletedTags,TotalTags;
    internal readonly bool Published;
    internal int Percent { get { return Published?100:Math.Min(99,(int)(100L*CompletedTags/TotalTags)); } }
    internal ExportProgress(int done,int total,bool published) { CompletedTags=done; TotalTags=total; Published=published; }
    internal string Description { get { return Percent+"%  ·  已读取标签 "+CompletedTags+" / "+TotalTags+(Published?"  ·  文件已保存":CompletedTags==TotalTags?"  ·  正在核对并保存":""); } }
}

// Owner drawing keeps completed work green even if Windows changes native progress themes.
internal sealed class GreenProgress : Control {
    int percent; string description="0%  ·  等待导出";
    internal int Percent { get { return percent; } }
    internal void Set(ExportProgress value) { percent=value.Percent; description=value.Description; Invalidate(); }
    internal void Reset() { percent=0; description="0%  ·  等待导出"; Invalidate(); }
    internal GreenProgress() {
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        Height=32; RightToLeft=RightToLeft.No;
        AccessibleName="导出实际进度";
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        if(Width<2 || Height<2) return;
        e.Graphics.Clear(Color.FromArgb(232,237,232));
        int width=(int)((Width-2)*percent/100.0);
        using(var fill=new SolidBrush(Color.FromArgb(40,167,69))) e.Graphics.FillRectangle(fill,1,1,width,Height-2);
        using(var border=new Pen(Color.FromArgb(160,174,160))) e.Graphics.DrawRectangle(border,0,0,Width-1,Height-1);
        TextRenderer.DrawText(e.Graphics,description,Font,ClientRectangle,Color.FromArgb(15,36,20),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
        AccessibleDescription=description;
    }
}

internal static class ExportFiles {
    internal const int TagRequestByteLimit=255; // Conservative CF_TEXT budget, including $Date,$Time and trailing NUL.
    internal const int MergeFanIn=32;
    internal static bool EquivalentParameter(string item,string sent,string actual) {
        if(item=="Tags" && actual.Length>=2 && actual[0]=='"' && actual[actual.Length-1]=='"') actual=actual.Substring(1,actual.Length-2);
        bool ignoreCase=new[]{"DataDir","DBDir","FileName","Tags","Duration","Interval"}.Contains(item);
        return string.Equals(sent,actual,ignoreCase?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
    }
    internal static void ReadBackParameters(IHistClient client,ExportOptions options,string staging) {
        // Read only: do not retry or trigger an export while probing the server's stored values.
        foreach(var expected in options.Parameters(staging)) {
            try {
                string actual=client.Request(expected.Key);
                SessionLog.Write("INFO","PARAMETER_READBACK","Item="+expected.Key+" Sent="+DebugLogFile.Quote(expected.Value)+" ReadBack="+DebugLogFile.Quote(actual)+" ExactMatch="+(actual==expected.Value)+" Equivalent="+EquivalentParameter(expected.Key,expected.Value,actual));
            } catch(Exception ex) {
                SessionLog.Exception("PARAMETER_READBACK_UNAVAILABLE_"+expected.Key,ex);
            }
        }
    }
    internal static List<ExportOptions> Batches(ExportOptions o) {
        string[] tags=o.Tags.Split(',').Skip(2).ToArray();
        // Aim for 32 requests, but split further whenever the actual encoded payload would be too long.
        int size=Math.Max(1,(tags.Length+31)/32);
        var result=new List<ExportOptions>();
        var current=new List<string>();
        foreach(string tag in tags) {
            int single=DdeClient.Ansi.GetByteCount("$Date,$Time,"+tag+"\0");
            if(single>TagRequestByteLimit) throw new ArgumentException("单个标记名过长，连同日期时间字段的请求长度为 "+single+" 字节，超过本程序单批 "+TagRequestByteLimit+" 字节的兼容预算："+tag);
            if(current.Count>0 && (current.Count>=size || DdeClient.Ansi.GetByteCount("$Date,$Time,"+string.Join(",",current)+","+tag+"\0")>TagRequestByteLimit)) {
                result.Add(CopyOptions(o,current)); current.Clear();
            }
            current.Add(tag);
        }
        if(current.Count>0) result.Add(CopyOptions(o,current));
        return result;
    }
    static ExportOptions CopyOptions(ExportOptions o,IEnumerable<string> tags) {
        return new ExportOptions {
            DataDir=o.DataDir,DbDir=o.DbDir,Output=o.Output,Start=o.Start,Duration=o.Duration,Interval=o.Interval,
            WireDateFormat=o.WireDateFormat,TimeoutSeconds=o.TimeoutSeconds,
            Tags="$Date,$Time,"+string.Join(",",tags),TagsNormalized=true
        };
    }
    internal static string Diagnostics(IHistClient client,ExportOptions o,string staging) {
        var text=new StringBuilder("HistData 诊断（失败后的即时状态，可能含上次操作遗留值）：\r\n");
        // The transport captured the original DDE code before these reads can reset it.
        foreach(string key in new[]{"Error","Status","WriteFile"}) {
            try { text.AppendLine(key+" = "+client.Request(key)); }
            catch(Exception ex) { text.AppendLine(key+" = 无法读取（"+ex.Message.Replace("\r\n"," ")+"）"); }
        }
        text.AppendLine("实际发送的参数：");
        foreach(var p in o.Parameters(staging)) text.AppendLine(p.Key+" = "+p.Value);
        text.AppendLine("Windows 短日期格式 = "+DateProtocol.WindowsPattern);
        text.AppendLine("Windows 当前格式示例 = "+DateProtocol.Format(o.Start,"Windows"));
        text.AppendLine("日期适配策略 = "+(o.WireDateFormat=="Windows"?"自动匹配；不需要改win操作系统短日期":"手动指定协议；可切回默认自动适配模式"));
        text.AppendLine("Windows 区域设置必须保持不变。若自动模式仍提示 STARTDATE / START DATE 项目无效，请提供完整日志，继续分析 HistData 的进程内日期解析兼容性；不自动重试或修改系统设置。");
        text.Append("若 Error 为 None、状态未更新或无法读取，请查看 HistData 窗口及同一时刻的 WWLogger / 日志查看器。");
        return text.ToString();
    }
    static void ValidateHeader(List<string> header,ExportOptions options,string path) {
        string[] tags=options.Tags.Split(',');
        if(header==null || header.Count!=tags.Length) throw new IOException("CSV 表头列数与请求标签数不符："+path);
        if(!string.Equals(header[0].TrimStart('$'),"Date",StringComparison.OrdinalIgnoreCase) || !string.Equals(header[1].TrimStart('$'),"Time",StringComparison.OrdinalIgnoreCase))
            throw new IOException("CSV 缺少预期的 Date / Time 列，无法安全合并："+path);
        for(int i=2;i<tags.Length;i++) if(!string.Equals(header[i],tags[i],StringComparison.OrdinalIgnoreCase))
            throw new IOException("CSV 标签列与请求不符，期望 "+tags[i]+"，实际 "+header[i]+"："+path);
    }
    internal static CsvStatistics ValidatePart(string path,ExportOptions options,Func<bool> stop) {
        SessionLog.Write("DEBUG","CSV_VALIDATE_BEGIN","Path="+path+" ExpectedTags="+options.Tags);
        var statistics=new CsvStatistics();
        using(var csv=new CsvInput(path)) {
            var header=csv.Read(); ValidateHeader(header,options,path);
            SessionLog.Write("DEBUG","CSV_HEADER","Path="+path+" Encoding="+csv.Encoding.WebName+" Columns="+header.Count+" Header="+string.Join(" | ",header));
            long count=0; List<string> row;
            string first=null,last=null;
            while((row=csv.Read())!=null) {
                if(stop()) throw new OperationCanceledException("已停止检查中间数据。");
                if(row.Count!=header.Count || row[0].Length==0 || row[1].Length==0)
                    throw new IOException("CSV 数据列不完整，第 "+(count+2)+" 行："+path);
                count++;
                bool hasValue=false;
                for(int column=2;column<row.Count;column++) {
                    if(string.IsNullOrWhiteSpace(row[column])) statistics.BlankValues++;
                    else hasValue=true;
                }
                if(hasValue) statistics.LastNonEmptyTime=row[0]+" "+row[1];
                last=string.Join(" | ",row); if(first==null) first=last;
            }
            if(count==0) throw new IOException("HistData 文件只有表头，没有数据记录。请检查历史记录覆盖的日期和标签。");
            SessionLog.Write("INFO","CSV_VALIDATE_OK","Path="+path+" Rows="+count+" FirstRow="+DebugLogFile.Quote(first)+" LastRow="+DebugLogFile.Quote(last));
            statistics.Rows=count;
            SessionLog.Write(statistics.BlankValues>0?"WARN":"INFO","CSV_VALUE_SUMMARY","Rows="+count+" BlankValues="+statistics.BlankValues+" LastNonEmptyTime="+statistics.LastNonEmptyTime);
        }
        return statistics;
    }
    internal static void Merge(IList<string> paths,IList<ExportOptions> batches,string staging,Func<bool> stop) {
        if(paths.Count>MergeFanIn) {
            var intermediate=new List<string>(); var combined=new List<ExportOptions>();
            bool succeeded=false;
            try {
                for(int first=0;first<paths.Count;first+=MergeFanIn) {
                    if(stop()) throw new OperationCanceledException("已停止分组合并，未发布正式文件。");
                    string file=staging+".merge_"+intermediate.Count.ToString("D3")+".csv";
                    intermediate.Add(file);
                    var options=batches.Skip(first).Take(MergeFanIn).ToArray();
                    Merge(paths.Skip(first).Take(MergeFanIn).ToArray(),options,file,stop);
                    combined.Add(CopyOptions(options[0],options.SelectMany(o=>o.Tags.Split(',').Skip(2))));
                }
                Merge(intermediate,combined,staging,stop); succeeded=true;
            } catch(Exception ex) { ex.Data["MergeRecovery"]="分组合并的中间文件：\r\n"+string.Join("\r\n",intermediate); throw; }
            finally {
                if(succeeded) foreach(string file in intermediate) try { File.Delete(file); } catch(Exception ex) { SessionLog.Exception("MERGE_TEMP_CLEANUP_FAILED",ex); }
            }
            return;
        }
        var clock=System.Diagnostics.Stopwatch.StartNew();
        SessionLog.Write("INFO","CSV_MERGE_BEGIN","Parts="+string.Join(" | ",paths)+" Destination="+staging);
        var readers=new List<CsvInput>();
        try {
            var header=new List<string>();
            foreach(string path in paths) readers.Add(new CsvInput(path));
            for(int i=0;i<readers.Count;i++) {
                var part=readers[i].Read(); ValidateHeader(part,batches[i],paths[i]);
                header.AddRange(i==0?part:part.Skip(2));
            }
            // Use the source encoding and preserve values as text: no numeric/date conversion.
            using(var output=new StreamWriter(new FileStream(staging,FileMode.CreateNew,FileAccess.Write,FileShare.None),readers[0].Encoding)) {
                WriteRecord(output,header);
                long rows=0;
                while(true) {
                    if(stop()) throw new OperationCanceledException("已停止合并；未生成正式文件。");
                    List<string> first=readers[0].Read();
                    var merged=first==null?null:new List<string>(first);
                    for(int i=1;i<readers.Count;i++) {
                        var row=readers[i].Read();
                        if((first==null)!=(row==null)) throw new IOException("各批次 CSV 行数不一致，停止合并，避免数据错位。");
                        if(first!=null) {
                            if(row.Count!=batches[i].Tags.Split(',').Length || first.Count!=batches[0].Tags.Split(',').Length)
                                throw new IOException("合并时发现 CSV 列数发生变化，未发布结果。");
                            if(row[0]!=first[0] || row[1]!=first[1]) throw new IOException("各批次时间轴不一致，第 "+(rows+2)+" 行，停止合并，避免数据错位。");
                            merged.AddRange(row.Skip(2));
                        }
                    }
                    if(first==null) break;
                    WriteRecord(output,merged); rows++;
                }
                if(rows==0) throw new IOException("没有可合并的数据记录。");
                SessionLog.Write("INFO","CSV_MERGE_WRITTEN","Rows="+rows+" Columns="+header.Count+" ElapsedMs="+clock.ElapsedMilliseconds);
            }
        } finally { foreach(var reader in readers) reader.Dispose(); }
    }
    static void WriteRecord(TextWriter writer,IEnumerable<string> fields) {
        writer.Write(string.Join(",",fields.Select(x=>x.IndexOfAny(new[]{',','"','\r','\n'})>=0?"\""+x.Replace("\"","\"\"")+"\"":x)));
        writer.Write("\r\n");
    }
}

// Streaming CSV reader supports quoted commas/newlines; it never loads a month into memory.
internal sealed class CsvInput : IDisposable {
    readonly StreamReader reader;
    internal Encoding Encoding { get { return reader.CurrentEncoding; } }
    internal CsvInput(string path) { reader=new StreamReader(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read),DdeClient.Ansi,true); }
    internal List<string> Read() {
        if(reader.Peek()<0) return null;
        var fields=new List<string>(); var value=new StringBuilder();
        bool quoted=false,closed=false;
        while(true) {
            int n=reader.Read();
            if(n<0) {
                if(quoted) throw new IOException("CSV 引号未闭合，文件可能不完整。");
                fields.Add(value.ToString()); return fields;
            }
            char c=(char)n;
            if(quoted) {
                if(c=='"') {
                    if(reader.Peek()=='"') { reader.Read(); value.Append('"'); }
                    else { quoted=false; closed=true; }
                } else value.Append(c);
            } else if(c==',') { fields.Add(value.ToString()); value.Length=0; closed=false; }
            else if(c=='\r' || c=='\n') {
                if(c=='\r' && reader.Peek()=='\n') reader.Read();
                if(fields.Count==0 && value.Length==0 && !closed) {
                    if(reader.Peek()<0) return null;
                    continue;
                }
                fields.Add(value.ToString()); return fields;
            } else if(c=='"' && value.Length==0 && !closed) quoted=true;
            else {
                if(closed || c=='"') throw new IOException("CSV 字段格式无效，无法安全合并。");
                value.Append(c);
            }
        }
    }
    public void Dispose() { reader.Dispose(); }
}
}
