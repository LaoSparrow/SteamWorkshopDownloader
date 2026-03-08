using System.Runtime.InteropServices;
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

    public static (long, long) GetDriveAvailableFreeSpace(string folderPath)
    {
        folderPath = Path.GetFullPath(folderPath);
        DriveInfo? targetDrive = null;
        var maxMatchLength = -1;

        var normalizedFolder = folderPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        // 根据操作系统决定路径匹配是否忽略大小写
        var pathComparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;

            var driveRoot = drive.RootDirectory.FullName;
            var normalizedRoot = driveRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (normalizedFolder.StartsWith(normalizedRoot, pathComparison))
            {
                if (normalizedRoot.Length > maxMatchLength)
                {
                    maxMatchLength = normalizedRoot.Length;
                    targetDrive = drive;
                }
            }
        }

        return targetDrive != null
            ? (targetDrive.AvailableFreeSpace, targetDrive.TotalSize)
            : (-1, -1);
    }

    public static void CleanUpDirectory(string folderPath, long expectedFreeSpace)
    {
        var parentDirInfo = new DirectoryInfo(folderPath);
        if (!parentDirInfo.Exists) 
            return;
        
        var (availDiskSpace, totalDiskSpace) = GetDriveAvailableFreeSpace(folderPath);
        if (availDiskSpace == -1 || totalDiskSpace == -1) 
            return;
        
        var currentFreeSpace = availDiskSpace;
        
        // 如果当前剩余空间已经达标，直接返回，无需清理
        if (currentFreeSpace >= expectedFreeSpace)
            return;
        
        foreach (var dir in parentDirInfo.GetDirectories().OrderBy(x => x.LastWriteTimeUtc))
        {
            var dirOccupiedSize = GetDirectorySize(dir.FullName);
            if (dirOccupiedSize == -1)
                continue;
            
            try
            {
                // 加入异常捕获，防止个别文件被占用导致整体中止
                dir.Delete(true);
                
                // 成功删除后，剩余空间增加
                currentFreeSpace += dirOccupiedSize;
                
                // 如果增加后满足了期望的剩余空间，终止清理
                if (currentFreeSpace >= expectedFreeSpace)
                    break;
            }
            catch (Exception)
            {
                // ignored
            }
        }
    }
    
    static long GetDirectorySize(string folderPath)
    {
        var dirInfo = new DirectoryInfo(folderPath);
        if (!dirInfo.Exists) return -1;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, 
            IgnoreInaccessible = true     
        };

        try
        {
            return dirInfo.EnumerateFiles("*", options).Sum(file => file.Length);
        }
        catch (Exception)
        {
            return -1;
        }
    }
}