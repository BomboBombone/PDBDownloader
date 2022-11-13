using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SymbolFetch;
using SymbolFetch.Helpers;

namespace PDBDownloader
{
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("Usage: PDBDownloader.exe input.exe C:/Output/path");
                return;
            }
            var builder = new UrlBuilder();
            var downloadURL = builder.BuildUrl(args[0]);

            if (string.IsNullOrEmpty(downloadURL))
            {
                Console.WriteLine("No debug information in header or missing pdb");
                return;
            }

            var fileInfo = new ResourceDownloader.FileInfo(downloadURL);
            if (!Directory.Exists(args[1])) { Directory.CreateDirectory(args[1]); }

            Console.WriteLine("Downloading pdb file...");

            var downloader = new ResourceDownloader();
            downloader.downloadFile(fileInfo, args[1]);

            Console.WriteLine("Successfully downloaded pdb file.\n Press any key to close...");
            Console.ReadLine();
        }
    }
}
