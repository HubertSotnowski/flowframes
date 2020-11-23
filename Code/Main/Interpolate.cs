﻿using Flowframes;
using Flowframes.Data;
using Flowframes.FFmpeg;
using Flowframes.IO;
using Flowframes.Magick;
using Flowframes.Main;
using Flowframes.OS;
using Flowframes.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Utils = Flowframes.Main.InterpolateUtils;

namespace Flowframes
{
    public class Interpolate
    {
        public enum OutMode { VidMp4, VidGif, ImgPng, ImgJpg }

        public static string currentTempDir;
        static string framesPath;
        public static int interpFactor;
        public static float currentInFps;
        public static float currentOutFps;

        public static string lastInputPath;

        public static bool cancelled = false;

        static Stopwatch sw = new Stopwatch();


        public static void SetFps(float inFps)
        {
            currentInFps = inFps;
            currentOutFps = inFps * interpFactor;
        }

        public static async void Start(string inPath, string outDir, int tilesize, OutMode outMode, AI ai)
        {
            cancelled = false;
            if (!Utils.InputIsValid(inPath, outDir, currentOutFps, interpFactor, tilesize)) return;     // General input checks
            if (!Utils.CheckAiAvailable(ai)) return;            // Check if selected AI pkg is installed
            lastInputPath = inPath;
            currentTempDir = Utils.GetTempFolderLoc(inPath, outDir);
            framesPath = Path.Combine(currentTempDir, "frames");
            if (!Utils.CheckDeleteOldTempFolder()) return;      // Try to delete temp folder if an old one exists
            if(!Utils.CheckPathValid(inPath)) return;           // Check if input path/file is valid
            Utils.PathAsciiCheck(inPath, outDir);
            Program.mainForm.SetStatus("Starting...");
            Program.mainForm.SetWorking(true);
            await Task.Delay(10);
            if (!IOUtils.IsPathDirectory(inPath))        // Input is video - extract frames first
                await ExtractFrames(inPath, framesPath);
            else
                IOUtils.Copy(inPath, framesPath);
            if (cancelled) return;
            sw.Restart();
            await Task.Delay(10);
            await FrameDedup.Run(framesPath);
            if (cancelled) return;
            string interpFramesDir = Path.Combine(currentTempDir, "frames-interpolated");
            string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(inPath) + IOUtils.GetAiSuffix(ai, interpFactor) + Utils.GetExt(outMode));
            int frames = IOUtils.GetAmountOfFiles(framesPath, false, "*.png");
            int targetFrameCount = frames * interpFactor;
            GetProgressByFrameAmount(interpFramesDir, targetFrameCount);
            if (cancelled) return;
            Program.mainForm.SetStatus("Running AI...");
            await RunAi(interpFramesDir, targetFrameCount, tilesize, ai);
            if (cancelled) return;
            Program.mainForm.SetProgress(100);
            await CreateVideo.FramesToVideo(interpFramesDir, outPath, outMode);
            Cleanup(interpFramesDir);
            Program.mainForm.SetWorking(false);
            Logger.Log("Total processing time: " + FormatUtils.Time(sw.Elapsed));
            sw.Stop();
            Program.mainForm.SetStatus("Done interpolating!");
        }

        public static async Task ExtractFrames(string inPath, string outPath, bool extractAudio = true)
        {
            Logger.Log("Extracting frames using FFmpeg...");
            await Task.Delay(10);
            bool rgb8 = Formats.preprocess.Contains(Path.GetExtension(inPath).ToLower());
            Program.mainForm.SetStatus("Extracting frames from video...");
            Size resolution = IOUtils.GetVideoRes(inPath);
            int maxHeight = Config.GetInt("maxVidHeight");
            if (resolution.Height > maxHeight)
            {
                float factor = (float)maxHeight / resolution.Height;
                int width = (resolution.Width * factor).RoundToInt();
                Logger.Log($"Video is bigger than the maximum - Downscaling to {width}x{maxHeight}.");
                await FFmpegCommands.VideoToFrames(inPath, outPath, width, maxHeight, false, rgb8, false, false);
            }
            else
            {
                await FFmpegCommands.VideoToFrames(inPath, outPath, false, rgb8, false, false);
            }
            /*
            if (AvProcess.lastOutputFfmpeg.ToLower().Contains("invalid"))
            {
                Cancel("Failed to read input video.");
                return;
            }
            */
            if (extractAudio)
            {
                string audioFile = Path.Combine(currentTempDir, "audio.m4a");
                if (!File.Exists(audioFile))
                    await FFmpegCommands.ExtractAudio(inPath, audioFile);
            }
            if (!cancelled && Config.GetBool("enableLoop"))
            {
                string lastFrame = IOUtils.GetHighestFrameNumPath(outPath);
                int newNum = Path.GetFileName(lastFrame).GetInt() + 1;
                string newFilename = Path.Combine(lastFrame.GetParentDir(), newNum.ToString().PadLeft(8, '0') + ".png");
                string firstFrame = Path.Combine(lastFrame.GetParentDir(), 1.ToString().PadLeft(8, '0') + ".png");
                File.Copy(firstFrame, newFilename);
                Logger.Log("Copied loop frame.");
            }
        }

        static async Task RunAi(string outpath, int targetFrames, int tilesize, AI ai)
        {
            Directory.CreateDirectory(outpath);

            if (ai.aiName == Networks.dainNcnn.aiName)
                await AiProcess.RunDainNcnn(framesPath, outpath, targetFrames, tilesize);

            if (ai.aiName == Networks.cainNcnn.aiName)
                await AiProcess.RunCainNcnnMulti(framesPath, outpath, tilesize, interpFactor);

            if (ai.aiName == Networks.rifeCuda.aiName)
                await AiProcess.RunRifeCuda(framesPath, interpFactor);
        }

        public static async void GetProgressByFrameAmount(string outdir, int target)
        {
            bool firstProgUpd = true;
            Program.mainForm.SetProgress(0);
            while (Program.busy)
            {
                if (AiProcess.processTime.IsRunning && Directory.Exists(outdir))
                {
                    if (firstProgUpd && Program.mainForm.IsInFocus())
                        Program.mainForm.GetMainTabControl().SelectedIndex = 2;
                    firstProgUpd = false;
                    string[] frames = Directory.GetFiles(outdir, $"*.{Utils.lastExt}");
                    if (frames.Length > 1)
                        Utils.UpdateInterpProgress(frames.Length, target, frames[frames.Length - 1]);
                    await Task.Delay(Utils.GetProgressWaitTime(frames.Length));
                }
                else
                {
                    await Task.Delay(200);
                }
            }
            Program.mainForm.SetProgress(-1);
        }

        public static void Cancel(string reason = "")
        {
            if (AiProcess.currentAiProcess != null && !AiProcess.currentAiProcess.HasExited)
                OSUtils.KillProcessTree(AiProcess.currentAiProcess.Id);
            if (AvProcess.lastProcess != null && !AvProcess.lastProcess.HasExited)
                OSUtils.KillProcessTree(AvProcess.lastProcess.Id);
            cancelled = true;
            Program.mainForm.SetStatus("Cancelled.");
            Program.mainForm.SetProgress(0);
            IOUtils.TryDeleteIfExists(currentTempDir);
            Program.mainForm.SetWorking(false);
            Logger.Log("Cancelled interpolation.");
            if (!string.IsNullOrWhiteSpace(reason))
                Utils.ShowMessage($"Cancelled:\n\n{reason}");
        }

        static void Cleanup(string interpFramesDir)
        {
            if (Config.GetBool("keepTempFolder")) return;
            Logger.Log("Deleting temporary files...");
            try
            {
                if (Config.GetBool("keepFrames"))
                    IOUtils.Copy(interpFramesDir, Path.Combine(currentTempDir.GetParentDir(), Path.GetFileName(currentTempDir).Replace("-temp", "-interpframes")));
                Directory.Delete(currentTempDir, true);
            }
            catch (Exception e)
            {
                Logger.Log("Cleanup Error: " + e.Message, true);
            }
        }
    }
}
