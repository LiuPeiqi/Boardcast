using System.Security.AccessControl;
using System.Security.Principal;
namespace Boardcast.Core;
public static class PrivateFile
{
    public static FileStream Create(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("No current user identity.");
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security);
        }
        return new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
    }
}
