using System;
using System.IO;

namespace PlayService
{
    public static class Utils
    {
        public static string GetFullPath(string basePath, string path)
        {
            if (!basePath.EndsWith(@"\") && !basePath.EndsWith(@"/"))
                basePath += Path.DirectorySeparatorChar;
            return (new Uri(new Uri(basePath), path)).LocalPath;
        }
    }
}
