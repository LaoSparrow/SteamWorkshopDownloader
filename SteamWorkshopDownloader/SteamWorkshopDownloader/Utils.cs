using System.Text;

namespace SteamWorkshopDownloader;

public static class Utils
{
    public static byte[] ReadTailBytes(string filePath, int tailBytes = 8192)
    {
        using var stream = new FileStream(filePath, FileMode.Open, 
            FileAccess.Read, FileShare.ReadWrite);
    
        long fileLength = stream.Length;
        if (fileLength == 0) return Array.Empty<byte>();
    
        long startPos = Math.Max(0, fileLength - tailBytes);
        stream.Seek(startPos, SeekOrigin.Begin);
    
        byte[] buffer = new byte[fileLength - startPos];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
    
        if (bytesRead != buffer.Length && startPos == 0)
            Array.Resize(ref buffer, bytesRead);
    
        return buffer;
    }
    
    public static string ReadTailLines(string filePath, int tailBytes = 8192)
    {
        byte[] tailData = ReadTailBytes(filePath, tailBytes);
        string tailText = Encoding.UTF8.GetString(tailData);
        
        // 从第一个完整换行符开始显示
        int lastNewline = tailText.IndexOf('\n');
        if (lastNewline > 0)
            tailText = tailText.Substring(lastNewline + 1);
    
        return string.Join('\n', tailText.Split('\n')
            .TakeLast(100));  // 调整行数
    }
}