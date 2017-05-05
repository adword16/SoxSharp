﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;


namespace SoxSharp
{
  /// <summary>
  /// Encapsulates all the information needed to launch
  /// </summary>
  public class Sox : IDisposable
  {
    /// <summary>
    /// Provides updated progress status while <see cref="Sox.Process"/> is being executed.
    /// </summary>
    public event EventHandler<ProgressEventArgs> OnProgress = null;


    /// <summary>
    /// Location of the SoX binary to be used by the library. If no path is specified (null or empty string) AND
    /// only on Windows platforms SoxSharp will use a binary copy included within the library (version XXX).
    /// On MacOSX and Linux systems, this property must be correctly set before any call to the <see cref="Process"/> method.
    /// </summary>
    /// <value>The binary path.</value>
    public string BinaryPath { get; set; }

    /// <summary>
    /// Size of all processing buffers (default is 8192).
    /// </summary>
    public UInt16? Buffer { get; set; }

    /// <summary>
    /// Enable parallel effects channels processing (where available).
    /// </summary>
    public bool? Multithreaded { get; set; }

    /// <summary>
    /// Input format options.
    /// </summary>
    public InputFormatOptions Input { get; protected set; }

    /// <summary>
    /// Output format options.
    /// </summary>
    public OutputFormatOptions Output { get; protected set; }


    private bool disposed_ = false;

    private static readonly byte[] SoxHash = new byte[] { 0x05, 0xd5, 0x00, 0x8a, 0x50, 0x56, 0xe2, 0x8e, 0x45, 0xad, 0x1e, 0xb7, 0xd7, 0xbe, 0x9f, 0x03 };
    private static readonly Regex RegexInfo = new Regex(@"Input File\s*: .+\r\nChannels\s*: (\d+)\r\nSample Rate\s*: (\d+)\r\nPrecision\s*: ([\s\S]+?)\r\nDuration\s*: (\d{2}:\d{2}:\d{2}\.?\d{2}?)[\s\S]+?\r\nFile Size\s*: (\d+\.?\d{0,2}?[k|M|G]?)\r\nBit Rate\s*: (\d+\.?\d{0,2}?k?)\r\nSample Encoding\s*: (.+)");
    private static readonly Regex RegexProgress = new Regex(@"In:(\d{1,3}\.?\d{0,2})%\s+(\d{2}:\d{2}:\d{2}\.?\d{0,2})\s+\[(\d{2}:\d{2}:\d{2}\.?\d{0,2})\]\s+Out:(\d+\.?\d{0,2}[k|M|G]?)");
    

    public Sox()
    {
      Input = new InputFormatOptions();
      Output = new OutputFormatOptions();
    }


    public void Dispose()
    {
      Dispose(true);
      GC.SuppressFinalize(this);
    }


    public FileInfo GetInfo(string inputFile)
    {
      if (!File.Exists(inputFile))
        throw new FileNotFoundException("File not found: " + inputFile);

      using (Process soxCmd = CreateSoxProcess())
      {
        soxCmd.StartInfo.RedirectStandardOutput = true;
        soxCmd.StartInfo.Arguments = "--info " + inputFile;
        soxCmd.Start();
        
        string result = soxCmd.StandardOutput.ReadToEnd();
        soxCmd.WaitForExit();

        if (result != null)
        {
          Match match = RegexInfo.Match(result);

          if (match.Success)
          {
            try
            {
              UInt16 channels = Convert.ToUInt16(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
              UInt32 sampleRate = Convert.ToUInt32(double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
              UInt16 sampleSize = Convert.ToUInt16(double.Parse(new string(match.Groups[3].Value.Where(Char.IsDigit).ToArray()), CultureInfo.InvariantCulture));
              TimeSpan duration = TimeSpan.ParseExact(match.Groups[4].Value, @"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture);
              UInt64 size = FormattedSize.ToUInt64(match.Groups[5].Value);
              UInt32 bitRate = FormattedSize.ToUInt32(match.Groups[6].Value);
              string encoding = match.Groups[7].Value;

              return new FileInfo(channels, sampleRate, sampleSize, duration, size, bitRate, encoding);
            }

            catch (Exception ex)
            {
              throw new SoxException("Unexpected output from SoX", ex);
            }
          }
          else
          {
            throw new SoxException(result);
          }
        }

        throw new SoxException("Unexpected output from SoX");
      }
    }


    public int Process(string inputFile, string outputFile)
    {
      using (Process soxCmd = CreateSoxProcess())
      {       
        soxCmd.ErrorDataReceived += ((sender, received) =>
        {
          if (received.Data != null)
          {
            if (OnProgress != null)
            {
              Match match = RegexProgress.Match(received.Data);

              if (match.Success)
              {
                try
                {
                  UInt16 progress = Convert.ToUInt16(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
                  TimeSpan processed = TimeSpan.ParseExact(match.Groups[2].Value, @"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture);
                  TimeSpan remaining = TimeSpan.ParseExact(match.Groups[3].Value, @"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture);
                  UInt64 outputSize = FormattedSize.ToUInt64(match.Groups[4].Value);
                  
                  OnProgress(sender, new ProgressEventArgs(progress, processed, remaining, outputSize));
                }

                catch (Exception ex)
                {
                  throw new SoxException("Unexpected output from SoX", ex);
                }                
              }
            }
          }
        });
        
        List<string> args = new List<string>();

        // Global options.

        if (Buffer.HasValue)
          args.Add("--buffer " + Buffer.Value);

        if (Multithreaded.HasValue)
          args.Add(Multithreaded.Value ? "--multi-threaded" : "--single-threaded");

        args.Add("--show-progress");

        // Input options.
        args.Add(Input.ToString());
        args.Add(inputFile);

        // Output options.
        args.Add(Output.ToString());
        args.Add(outputFile);

        soxCmd.StartInfo.Arguments = String.Join(" ", args);

        try
        {
          soxCmd.Start();
          soxCmd.BeginErrorReadLine();
          soxCmd.WaitForExit();

          return soxCmd.ExitCode;
        }

        catch (Exception ex)
        {
          throw new SoxException("Cannot spawn Sox process", ex);
        }
      }
    }


    protected Process CreateSoxProcess()
    {
      string soxExecutable;

      if ((Environment.OSVersion.Platform == PlatformID.MacOSX) ||
          (Environment.OSVersion.Platform == PlatformID.Unix))
      {
        if (String.IsNullOrEmpty(BinaryPath))
          throw new SoxException("SoX path not specified");

        if (File.Exists(BinaryPath))
          soxExecutable = BinaryPath;
        else
          throw new FileNotFoundException("SoX executable not found");
      }

      else
      {
        if (String.IsNullOrEmpty(BinaryPath))
        {
          // The SoX executable is directly extracted from the library resources
          // and only if it was not previously extracted (file MD5 hash check is
          // performed to ensure it is the expected one).

          try
          {
            soxExecutable = Path.Combine(Path.GetTempPath(), "sox.exe");

            if (!File.Exists(soxExecutable))
              File.WriteAllBytes(soxExecutable, SoxSharp.Resources.sox);
            else
            {
              using (MD5 md5 = MD5.Create())
              {
                using (FileStream stream = File.OpenRead(soxExecutable))
                {
                  byte[] hash = md5.ComputeHash(stream);

                  if (!SoxHash.SequenceEqual(hash))
                    File.WriteAllBytes(soxExecutable, SoxSharp.Resources.sox);
                }
              }
            }
          }

          catch (Exception ex)
          {
            throw new SoxException("Cannot extract SoX executable", ex);
          }
        }
        else
        {
          if (File.Exists(BinaryPath))
            soxExecutable = BinaryPath;
          else
            throw new FileNotFoundException("SoX executable not found");
        }
      }

      Process soxProc = new Process();

      soxProc.StartInfo.FileName = soxExecutable;
      soxProc.StartInfo.ErrorDialog = false;
      soxProc.StartInfo.CreateNoWindow = true;
      soxProc.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
      soxProc.StartInfo.UseShellExecute = false;
      soxProc.StartInfo.RedirectStandardError = true;
      soxProc.EnableRaisingEvents = true;

      return soxProc;
    }


    private void Dispose(bool disposing)
    {
      if (disposed_)
        return;

      disposed_ = true;
    }
  }
}
