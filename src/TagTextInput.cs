using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace HistDataExport {
internal static class TagInputRules {
    internal const int MaxCharacters=1048576;
    internal static ArgumentException TooLong(long actual,string source) {
        var error=new ArgumentException(source+"字符长度超限。\r\n实际字符数："+actual.ToString("N0",CultureInfo.InvariantCulture)+
            "\r\n允许上限："+MaxCharacters.ToString("N0",CultureInfo.InvariantCulture)+
            "\r\n超出字符数："+(actual-MaxCharacters).ToString("N0",CultureInfo.InvariantCulture)+
            "\r\n请缩短标签清单或拆分文件后重试。本次不截断或替换原有内容。");
        error.Data["ActualCharacters"]=actual; error.Data["MaximumCharacters"]=MaxCharacters; error.Data["ExceededCharacters"]=actual-MaxCharacters;
        return error;
    }
    internal static void ValidateLength(long actual,string source) { if(actual>MaxCharacters) throw TooLong(actual,source); }
}

internal sealed class TagTextBox : TextBox {
    internal event Action<long> PasteRejected;
    internal TagTextBox() { MaxLength=TagInputRules.MaxCharacters; }
    internal bool AcceptPaste(string value) {
        long proposed=(long)TextLength-SelectionLength+(value??"").Length;
        if(proposed<=MaxLength) return true;
        if(PasteRejected!=null) PasteRejected(proposed);
        return false;
    }
    protected override bool ProcessCmdKey(ref Message msg,Keys keyData) {
        if(keyData==(Keys.Control|Keys.V) || keyData==(Keys.Shift|Keys.Insert)) { Paste(); return true; }
        return base.ProcessCmdKey(ref msg,keyData);
    }
    protected override void WndProc(ref Message m) {
        if(m.Msg==0x0302) { // Reject an oversized paste as a whole; native MaxLength would silently clip it.
            try { if(Clipboard.ContainsText() && !AcceptPaste(Clipboard.GetText())) return; }
            catch(System.Runtime.InteropServices.ExternalException ex) { SessionLog.Exception("TAG_CLIPBOARD_UNAVAILABLE",ex); }
        }
        base.WndProc(ref m);
    }
}

internal static class TagTextImport {
    internal static string ReadFile(string path) {
        bool ini=string.Equals(Path.GetExtension(path),".ini",StringComparison.OrdinalIgnoreCase);
        try { return Read(path,new UTF8Encoding(false,true),ini); }
        catch(DecoderFallbackException) { return Read(path,Encoding.GetEncoding(936),ini); }
    }
    static string Read(string path,Encoding encoding,bool ini) {
        var text=new StringBuilder(); long length=0;
        string prefix="txtTags="; int matched=0; bool skipLine=false,readingValue=!ini,found=!ini,ended=false;
        using(var reader=new StreamReader(path,encoding,true)) {
            var buffer=new char[8192]; int count;
            while(!ended && (count=reader.Read(buffer,0,buffer.Length))>0) {
                if(!ini) {
                    length+=count;
                    if(length<=TagInputRules.MaxCharacters) text.Append(buffer,0,count);
                    continue;
                }
                for(int i=0;i<count;i++) {
                    char c=buffer[i];
                    if(readingValue) {
                        if(c=='\r' || c=='\n') { ended=true; break; }
                        length++;
                        if(length<=TagInputRules.MaxCharacters) text.Append(c);
                    } else if(c=='\r' || c=='\n') { matched=0; skipLine=false; }
                    else if(!skipLine) {
                        if(char.ToUpperInvariant(c)==char.ToUpperInvariant(prefix[matched])) {
                            matched++;
                            if(matched==prefix.Length) { readingValue=true; found=true; }
                        } else skipLine=true;
                    }
                }
            }
            if(!found) throw new ArgumentException("INI 中未找到 txtTags；只读取标签，不读取其他参数。");
            SessionLog.Write("INFO","TAG_IMPORT_LENGTH","Path="+path+" Encoding="+reader.CurrentEncoding.WebName+" Characters="+length+" Limit="+TagInputRules.MaxCharacters+" Ini="+ini);
        }
        // Keep counting after the limit without retaining the entire oversized file in memory.
        TagInputRules.ValidateLength(length,"导入的目标标记名");
        return text.ToString();
    }
}
}
