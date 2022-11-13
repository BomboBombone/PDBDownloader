using SymbolFetch.Helpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using static SymbolFetch.ResourceDownloader;

namespace SymbolFetch.Helpers
{
    public static class Constants
    {

        #region Settings
        public static string SymbolServer = @"http://msdl.microsoft.com/download/symbols";
        public static string DownloadFolder = @"c:\symbols";
        public static bool EnableBulkDownload = false;
        #endregion
    }
}

namespace SymbolFetch
{
    #region FileDownloader
    class ResourceDownloader
    {

        #region Nested Types
        public struct FileInfo
        {
            public string Path;
            public string Name;
            public string PdbGuid;
            public bool IsCompressed;

            public FileInfo(String path)
            {
                this.Path = path;
                this.Name = this.Path.Split("/"[0])[this.Path.Split("/"[0]).Length - 1];
                this.PdbGuid = this.Path.Split("/"[0])[this.Path.Split("/"[0]).Length - 2];
                this.IsCompressed = false;
            }
            
            public void SetPath(string path)
            {
                this.Path = path;
            }

            public void SetName()
            {
                this.Name = this.Path.Split("/"[0])[this.Path.Split("/"[0]).Length - 1];
            }
            public void SetNameUsingPath(string path)
            {
                this.Name = path;
            }

        }


        private enum Event
        {
            CalculationFileSizesStarted,

            FileSizesCalculationComplete,
            DeletingFilesAfterCancel,

            FileDownloadAttempting,
            FileDownloadStarted,
            FileDownloadStopped,
            FileDownloadSucceeded,

            ProgressChanged
        };

        private enum InvokeType
        {
            EventRaiser,
            FileDownloadFailedRaiser,
            CalculatingFileNrRaiser
        };
        #endregion

        #region Fields
        private const Int32 default_decimals = 2;

        // Delegates
        public delegate void FailEventHandler(object sender, Exception ex);
        public delegate void CalculatingFileSizeEventHandler(object sender, Int32 fileNr);

        // The download worker
        private BackgroundWorker bgwDownloader = new BackgroundWorker();

        // Preferences
        private Int32 m_packageSize;
        public string DownloadLocation;

        // Data
        private String m_localDirectory;
        private List<FileInfo> m_files = new List<FileInfo>();

        #endregion

        #region Constructors
        public ResourceDownloader()
        {
            this.initizalize(false);
        }

        public ResourceDownloader(Boolean supportsProgress)
        {
            this.initizalize(supportsProgress);
        }

        private void initizalize(Boolean supportsProgress)
        {
            // Set the default class preferences
            this.PackageSize = 4096;
            this.DownloadLocation = !string.IsNullOrEmpty(Constants.DownloadFolder)? Constants.DownloadFolder: "C:\\symcache";
        }
        #endregion

        #region Public methods

        public void SetDownloadLocation(string path)
        {
            DownloadLocation = path;
        }

        #region Size formatting functions
        public static string FormatSizeBinary(Int64 size)
        {
            return ResourceDownloader.FormatSizeBinary(size, default_decimals);
        }
        
        public static string FormatSizeBinary(Int64 size, Int32 decimals)
        {
            String[] sizes = { "B", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB" };
            Double formattedSize = size;
            Int32 sizeIndex = 0;
            while (formattedSize >= 1024 && sizeIndex < sizes.Length)
            {
                formattedSize /= 1024;
                sizeIndex += 1;
            }
            return Math.Round(formattedSize, decimals) + sizes[sizeIndex];
        }

        public static string FormatSizeDecimal(Int64 size)
        {
            return ResourceDownloader.FormatSizeDecimal(size, default_decimals);
        }

        public static string FormatSizeDecimal(Int64 size, Int32 decimals)
        {
            String[] sizes = { "B", "kB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB" };
            Double formattedSize = size;
            Int32 sizeIndex = 0;
            while (formattedSize >= 1000 && sizeIndex < sizes.Length)
            {
                formattedSize /= 1000;
                sizeIndex += 1;
            }
            return Math.Round(formattedSize, decimals) + sizes[sizeIndex];
        }
        public void downloadFile(FileInfo fileInfo, string location)
        {
            bool headVerb = false;
            bool fileptr = false;

            FileInfo file = fileInfo;

            Int64 size = 0;
            long m_currentFileSize;
            int m_currentFileProgress;

            Byte[] readBytes = new Byte[this.PackageSize];
            Int32 currentPackageSize;
            Exception exc = null;

            FileStream writer;
            string dirPath = DownloadLocation + "\\" + file.Name + "\\" + file.PdbGuid;
            if (location != null && location != "")
            {
                dirPath = location;
            }
            string downloadUrl = fileInfo.Path;

            HttpWebRequest webReq;
            HttpWebResponse webResp = null;

            try
            {
                webReq = (HttpWebRequest)System.Net.WebRequest.Create(downloadUrl);
                webReq.UserAgent = Constants.SymbolServer;
                webResp = (HttpWebResponse)webReq.GetResponse();
                if (webResp.StatusCode == HttpStatusCode.NotFound)
                {
                    webResp = Retry(fileInfo, headVerb);

                    if (webResp.StatusCode == HttpStatusCode.OK)
                    {
                        file.IsCompressed = true;
                        size = webResp.ContentLength;
                    }

                    if (webResp.StatusCode == HttpStatusCode.NotFound)
                    {
                        webResp = RetryFilePointer(fileInfo);
                        fileptr = true;
                    }

                    if (webResp.StatusCode != HttpStatusCode.OK)
                    {
                        if (!FailedFiles.ContainsKey(file.Name))
                            FailedFiles.Add(file.Name, " - " + webResp.StatusCode + "  " + webResp.StatusDescription);
                    }
                }
                else if (webResp.StatusCode == HttpStatusCode.OK)
                    size = webResp.ContentLength;

            }
            catch (Exception ex)
            {
                exc = ex;
                WriteToLog(file.Name, exc);
            }
            if (webResp.StatusCode == HttpStatusCode.OK)
            {
                Directory.CreateDirectory(dirPath);

                if (fileptr)
                {
                    string filePath = dirPath + "\\" +
                        file.Name;
                    string srcFile = null;
                    FileStream reader;
                    size = ProcessFileSize(webResp, out srcFile);
                    m_currentFileSize = size;

                    if (srcFile != null)
                    {
                        reader = new FileStream(srcFile, FileMode.Open, FileAccess.Read);
                        writer = new FileStream(filePath,
                            System.IO.FileMode.Create);

                        m_currentFileProgress = 0;
                        while (m_currentFileProgress < size && !bgwDownloader.CancellationPending)
                        {

                            currentPackageSize = reader.Read(readBytes, 0, this.PackageSize);

                            writer.Write(readBytes, 0, currentPackageSize);
                        }
                        reader.Close();
                        writer.Close();
                        //end
                    }
                }
                else
                {
                    m_currentFileSize = size;
                    //string name;
                    if (file.IsCompressed)
                    {
                        file.Name = ProbeWithUnderscore(file.Name);
                    }
                    string filePath = dirPath + "\\" +
                        file.Name;
                    writer = new FileStream(filePath,
                        System.IO.FileMode.Create);

                    if (exc != null)
                    {
                        bgwDownloader.ReportProgress((Int32)InvokeType.FileDownloadFailedRaiser, exc);
                    }
                    else
                    {
                        m_currentFileProgress = 0;
                        while (m_currentFileProgress < size && !bgwDownloader.CancellationPending)
                        {
                            currentPackageSize = webResp.GetResponseStream().Read(readBytes, 0, this.PackageSize);
                            m_currentFileProgress += currentPackageSize;
                            writer.Write(readBytes, 0, currentPackageSize);
                        }

                        writer.Close();

                        webResp.Close();
                        if (file.IsCompressed)
                        {
                            HandleCompression(filePath);
                        }

                    }
                }
            }
        }
        #endregion

        private HttpWebResponse Retry(FileInfo fileInfo, bool headVerb)
        {
            string path = fileInfo.Path;
            path = ProbeWithUnderscore(path);
            var webReq = (HttpWebRequest)System.Net.WebRequest.Create(path);
            webReq.UserAgent = Constants.SymbolServer;
            if(headVerb)
                webReq.Method = "HEAD";
            return (HttpWebResponse)webReq.GetResponse();
        }

        private HttpWebResponse RetryFilePointer(FileInfo fileInfo)
        {
            string path = fileInfo.Path;
            path = ProbeWithFilePointer(path);
            var webReq = (HttpWebRequest)System.Net.WebRequest.Create(path);
            webReq.UserAgent = Constants.SymbolServer;
            return (HttpWebResponse)webReq.GetResponse();
        } 

        private long ProcessFileSize(HttpWebResponse webResp, out string filePath)
        {
            long length = 0;
            filePath = null;
            Stream receiveStream = webResp.GetResponseStream();
            Encoding encode = System.Text.Encoding.GetEncoding("utf-8");
            StreamReader readStream = new StreamReader(receiveStream, encode);
            Char[] read = new Char[webResp.ContentLength];
            readStream.Read(read, 0, (int)webResp.ContentLength);

            string file = new string(read, 0, (int)webResp.ContentLength);

            if (file.Contains("PATH"))
            {
                file = file.Substring(5, file.Length - 5); //Removing PATH: from the output

                try
                {
                    System.IO.FileInfo fInfo = new System.IO.FileInfo(file);
                    if (fInfo.Exists)   
                    {
                        length = fInfo.Length;
                        filePath = file;
                    }
                }
                catch(Exception ex)
                {
                    WriteToLog(file, ex);
                }
            }
            else
            {
                int position= webResp.ResponseUri.PathAndQuery.IndexOf(".pdb");
                string fileName = webResp.ResponseUri.PathAndQuery.Substring(1, position + 3);
                if (!FailedFiles.ContainsKey(fileName))
                    FailedFiles.Add(fileName, " - No matching PDBs found - " + file);
            }

            return length;
        }

        private void DownloadFile(string srcFile, string filePath)
        {
            File.Copy(srcFile, filePath, true);
        }

        private static string ProbeWithUnderscore(string path)
        {
            path = path.Remove(path.Length - 1);
            path = path.Insert(path.Length, "_");
            return path;
        }

        private static string ProbeWithFilePointer(string path)
        {
            int position  = path.LastIndexOf('/');
            path = path.Remove(position, (path.Length - position));
            path = path.Insert(path.Length, "/file.ptr");
            return path;
        }

        public static void WriteToLog(string fileName, Exception exc)
        {
            using (FileStream fs = new FileStream("Log.txt", FileMode.Append))
            using (StreamWriter sr = new StreamWriter(fs))
            {
                sr.WriteLine(DateTime.Now.ToString() + "   " + fileName + " - " + exc.Message);
            }
        }

        public static void WriteToLog(string fileName, string text)
        {
            using (FileStream fs = new FileStream("Log.txt", FileMode.Append))
            using (StreamWriter sr = new StreamWriter(fs))
            {
                sr.WriteLine(DateTime.Now.ToString() + "   " + fileName + " - " + text);
            }
        }


        private void HandleCompression(string filePath)
        {
            string uncompressedFilePath = filePath.Remove(filePath.Length - 1);
            uncompressedFilePath = uncompressedFilePath.Insert(uncompressedFilePath.Length, "b");
            string args = string.Format("expand {0} {1}", "\"" + filePath + "\"", "\"" + uncompressedFilePath + "\"");

            Match m = Regex.Match(args, "^\\s*\"(.*?)\"\\s*(.*)");
            if (!m.Success)
                m = Regex.Match(args, @"\s*(\S*)\s*(.*)");    // thing before first space is command

            Process process = new Process();
            ProcessStartInfo startInfo = new ProcessStartInfo(m.Groups[1].Value, m.Groups[2].Value);

            startInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
            
            startInfo.UseShellExecute = false;
            startInfo.Verb = "runas";
            startInfo.CreateNoWindow = true;
            process.StartInfo = startInfo;
            
            try
            {
                var started = process.Start();
                if (started)
                {
                    process.WaitForExit(600000);
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                WriteToLog(filePath, ex);
            }
        }

        private void cleanUpFiles(Int32 start, Int32 length)
        {
            Int32 last = length < 0 ? this.Files.Count - 1 : start + length - 1;

            for (Int32 fileNr = start; fileNr <= last; fileNr++)
            {
                String fullPath = this.LocalDirectory + "\\" + this.Files[fileNr].Name;
                if (System.IO.File.Exists(fullPath)) { System.IO.File.Delete(fullPath); }
            }
        }
        #endregion

        #region Properties
        public List<FileInfo> Files
        {
            get { return m_files; }
            set
            {
                if (this.Files != null) m_files = value;
            }
        }

        public Dictionary<string,string> FailedFiles = new Dictionary<string, string>();

        public String LocalDirectory
        {
            get { return m_localDirectory; }
            set
            {
                if (this.LocalDirectory != value) { m_localDirectory = value; }
            }
        }

        public Int32 PackageSize
        {
            get { return m_packageSize; }
            set
            {
                if (value > 0)
                {
                    m_packageSize = value;
                }
                else
                {
                    throw new InvalidOperationException("The PackageSize needs to be greather then 0");
                }
            }
        }
        #endregion

    }
    #endregion

}