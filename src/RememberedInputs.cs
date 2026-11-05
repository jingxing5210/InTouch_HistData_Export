using System;
using System.IO;
using System.Text;
using System.Xml;

namespace HistDataExport {
internal sealed class RememberedInputs {
    internal string DataDir="",DbDir="",Output="",Tags="";
}

internal static class InputStore {
    internal static string DefaultPath {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"InTouchHistDataExport","last-inputs.xml"); }
    }
    internal static RememberedInputs Load(string path,out string warning) {
        SessionLog.Write("DEBUG","INPUTS_LOAD_BEGIN","Path="+path);
        warning=null;
        if(!File.Exists(path) && !File.Exists(path+".bak")) return null;
        try { return Read(path); }
        catch(Exception ex) {
            SessionLog.Exception("INPUTS_LOAD_FAILED",ex);
            warning="无法读取上次输入："+ex.Message;
            try {
                var recovered=Read(path+".bak");
                SessionLog.Write("WARN","INPUTS_BACKUP_RESTORED","Path="+path+".bak");
                warning+="；已从备用文件恢复。"; return recovered;
            } catch { warning+="；请重新填写，程序仍可正常使用。"; return null; }
        }
    }
    static RememberedInputs Read(string path) {
        var settings=new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=4*1024*1024 };
        var xml=new XmlDocument { XmlResolver=null,PreserveWhitespace=true };
        using(var reader=XmlReader.Create(path,settings)) xml.Load(reader);
        if(xml.DocumentElement==null || xml.DocumentElement.Name!="LastInputs") throw new IOException("输入记忆文件格式不正确。");
        var root=xml.DocumentElement;
        foreach(string name in new[]{"DataDir","DbDir","Output","Tags"}) if(root[name]==null) throw new IOException("输入记忆文件缺少字段："+name);
        var value=new RememberedInputs { DataDir=root["DataDir"].InnerText,DbDir=root["DbDir"].InnerText,Output=root["Output"].InnerText,Tags=root["Tags"].InnerText };
        SessionLog.Write("DEBUG","INPUTS_READ","Path="+path+" DataDir="+DebugLogFile.Quote(value.DataDir)+" DbDir="+DebugLogFile.Quote(value.DbDir)+" Output="+DebugLogFile.Quote(value.Output)+" Tags="+DebugLogFile.Quote(value.Tags));
        return value;
    }
    internal static void Save(string path,RememberedInputs inputs) {
        SessionLog.Write("DEBUG","INPUTS_SAVE_BEGIN","Path="+path);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            var settings=new XmlWriterSettings { Encoding=new UTF8Encoding(false),Indent=true,NewLineHandling=NewLineHandling.Entitize };
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using(var writer=XmlWriter.Create(stream,settings)) {
                writer.WriteStartElement("LastInputs"); writer.WriteAttributeString("version","1");
                writer.WriteElementString("DataDir",inputs.DataDir??""); writer.WriteElementString("DbDir",inputs.DbDir??"");
                writer.WriteElementString("Output",inputs.Output??""); writer.WriteElementString("Tags",inputs.Tags??"");
                writer.WriteEndElement();
            }
            // Validate the new file before atomically replacing the previous successful save.
            Read(temp);
            if(File.Exists(path)) File.Replace(temp,path,path+".bak");
            else File.Move(temp,path);
            SessionLog.Write("DEBUG","INPUTS_SAVE_OK","Path="+path);
        } finally { if(File.Exists(temp)) File.Delete(temp); }
    }
}
}
