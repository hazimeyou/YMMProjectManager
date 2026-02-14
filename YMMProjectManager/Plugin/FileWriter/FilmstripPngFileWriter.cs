using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Output;
using YukkuriMovieMaker.Plugin.FileWriter;
using YukkuriMovieMaker.Project;

namespace YMMProjectManager.Plugin.FileWriter;

public sealed class FilmstripPngFileWriter : IVideoFileWriter
{
    private const int SampleCount = 64;

    private readonly FileLogger logger;
    private readonly int[] targetFrames;
    private readonly Dictionary<int, List<int>> frameToSlots;
    private readonly ThumbnailSequenceFrameRenderer renderer = new();
    private readonly string outputDirectory;
    private readonly string outputBaseName;
    private readonly string? cacheDirectory;
    private readonly bool forceRegenerate;
    private readonly VideoInfo videoInfo;
    private readonly Stopwatch totalStopwatch = Stopwatch.StartNew();
    private readonly Stopwatch initStopwatch = Stopwatch.StartNew();
    private readonly Stopwatch encodeWriteStopwatch = new();

    private int frameIndex;
    private bool disposed;
    private byte[]? lastSampleFrameBytes;
    private int callbackCount;
    private int callbackHitCount;
    private int usedSlotCount;
    private int fallbackSlotCount;
    private int heavyWorkCount;
    private int savedFileCount;
    private int cacheReuseCount;

    public FilmstripPngFileWriter(
        string outputPath,
        VideoInfo videoInfo,
        int lengthFrames,
        string? cacheDirectory,
        bool forceRegenerate,
        FileLogger logger)
    {
        this.logger = logger;
        this.videoInfo = videoInfo;
        (outputDirectory, outputBaseName) = ResolveOutputInfo(outputPath);
        this.cacheDirectory = cacheDirectory;
        this.forceRegenerate = forceRegenerate;

        targetFrames = CreateSampleFrames(lengthFrames);
        frameToSlots = BuildFrameToSlots(targetFrames);
        initStopwatch.Stop();
        logger.Info(
            $"Writer init done. ms={initStopwatch.ElapsedMilliseconds}, lengthFrames={lengthFrames}, targetCount={targetFrames.Length}, uniqueTargetFrames={frameToSlots.Count}");
    }

    public VideoFileWriterSupportedStreams SupportedStreams => VideoFileWriterSupportedStreams.Video;

    public void WriteAudio(float[] samples)
    {
        // Video-only writer.
    }

    public void WriteVideo(byte[] frame)
    {
        callbackCount++;

        if (disposed)
        {
            frameIndex++;
            return;
        }

        try
        {
            if (!frameToSlots.TryGetValue(frameIndex, out var slots))
            {
                frameIndex++;
                return;
            }

            callbackHitCount++;
            lastSampleFrameBytes = frame;
            foreach (var slot in slots)
            {
                WriteOneThumbnail(slot, frame);
                usedSlotCount++;
            }
        }
        catch (Exception ex)
        {
            logger.Error($"WriteVideo failed at frameIndex={frameIndex}", ex);
        }
        finally
        {
            frameIndex++;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            for (var slot = 0; slot < SampleCount; slot++)
            {
                var outputFile = GetOutputFilePath(slot);
                if (File.Exists(outputFile))
                {
                    continue;
                }

                if (lastSampleFrameBytes is not null)
                {
                    WriteOneThumbnail(slot, lastSampleFrameBytes);
                    fallbackSlotCount++;
                }
                else
                {
                    WriteBlankThumbnail(slot);
                    fallbackSlotCount++;
                }
            }

            totalStopwatch.Stop();
            logger.Info(
                $"Writer dispose done. callbacks={callbackCount}, callbackHits={callbackHitCount}, usedSlots={usedSlotCount}, fallbackSlots={fallbackSlotCount}, heavyWork={heavyWorkCount}, saved={savedFileCount}, cacheReused={cacheReuseCount}, encodeWriteMs={encodeWriteStopwatch.ElapsedMilliseconds}, totalMs={totalStopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            logger.Error("Dispose failed while finalizing filmstrip output", ex);
        }
    }

    private void WriteOneThumbnail(int index, byte[] frame)
    {
        var outputFile = GetOutputFilePath(index);
        var cacheFile = GetCacheFilePath(index);

        if (!forceRegenerate && cacheFile is not null && File.Exists(cacheFile))
        {
            CopyFile(cacheFile, outputFile);
            cacheReuseCount++;
            savedFileCount++;
            return;
        }

        heavyWorkCount++;
        encodeWriteStopwatch.Start();
        renderer.SaveThumbnailPng(frame, videoInfo.Width, videoInfo.Height, outputFile);
        encodeWriteStopwatch.Stop();
        savedFileCount++;

        if (cacheFile is not null)
        {
            CopyFile(outputFile, cacheFile);
        }
    }

    private void WriteBlankThumbnail(int index)
    {
        var outputFile = GetOutputFilePath(index);
        var cacheFile = GetCacheFilePath(index);

        if (!forceRegenerate && cacheFile is not null && File.Exists(cacheFile))
        {
            CopyFile(cacheFile, outputFile);
            cacheReuseCount++;
            savedFileCount++;
            return;
        }

        heavyWorkCount++;
        encodeWriteStopwatch.Start();
        ThumbnailSequenceFrameRenderer.SaveBlankPng(outputFile);
        encodeWriteStopwatch.Stop();
        savedFileCount++;
        if (cacheFile is not null)
        {
            CopyFile(outputFile, cacheFile);
        }
    }

    private string GetOutputFilePath(int index)
    {
        Directory.CreateDirectory(outputDirectory);
        return Path.Combine(outputDirectory, $"{outputBaseName}_{index:000}.png");
    }

    private string? GetCacheFilePath(int index)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            return null;
        }

        return Path.Combine(cacheDirectory, $"{index:000}.png");
    }

    private static void CopyFile(string source, string destination)
    {
        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.Copy(source, destination, true);
    }

    private static int[] CreateSampleFrames(int lengthFrames)
    {
        var totalFrames = Math.Max(1, lengthFrames);
        var samples = new int[SampleCount];
        for (var i = 0; i < SampleCount; i++)
        {
            samples[i] = totalFrames == 1
                ? 0
                : (int)Math.Round(i * (totalFrames - 1d) / (SampleCount - 1d));
        }

        return samples;
    }

    private static Dictionary<int, List<int>> BuildFrameToSlots(int[] targets)
    {
        var map = new Dictionary<int, List<int>>();
        for (var slot = 0; slot < targets.Length; slot++)
        {
            var frame = targets[slot];
            if (!map.TryGetValue(frame, out var list))
            {
                list = [];
                map[frame] = list;
            }

            list.Add(slot);
        }

        return map;
    }

    private static (string directory, string baseName) ResolveOutputInfo(string path)
    {
        if (Directory.Exists(path))
        {
            return (path, "filmstrip");
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Environment.CurrentDirectory;
        }

        var baseName = Path.GetFileNameWithoutExtension(fullPath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "filmstrip";
        }

        return (directory, baseName);
    }
}
