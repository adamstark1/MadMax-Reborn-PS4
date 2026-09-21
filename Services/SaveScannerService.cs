using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using MadMaxReborn.Models;

namespace MadMaxReborn.Services;

public class SaveScannerService
{
    private static readonly byte[] JeetDbHash = [0x53, 0xAA, 0x89, 0xA5];
    private static readonly byte[] JeetObjPattern = [0x04, 0x00, 0x00, 0x00, 0x56, 0x12, 0x48, 0x20];
    private static readonly byte[] GutgashObjPattern = [0x04, 0x00, 0x00, 0x00, 0xB7, 0x23, 0x60, 0x6F];
    private static readonly byte[] PinkEyeHash = [0x3D, 0xE0, 0x2C, 0x6F];
    private static readonly byte[] DeepFriahHash = [0xAA, 0xD0, 0x5A, 0xA3];

    private static readonly byte[] PennySavedHash = [0x39, 0x86, 0x60, 0x6C];
    private static readonly byte[] DividendHash = [0x9D, 0x83, 0x01, 0xAD];

    public string? FindLatestSaveFile(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return null;

        var saveFiles = Directory.GetFiles(directoryPath, "GAMESAVE*")
            .Where(f =>
            {
                string name = Path.GetFileName(f);
                if (!name.StartsWith("GAMESAVE", StringComparison.OrdinalIgnoreCase))
                    return false;

                string numPart = name.Substring(8);
                return int.TryParse(numPart, out int num) && num >= 1 && num <= 10;
            })
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        return saveFiles.FirstOrDefault()?.FullName;
    }

    public SaveAnalysisResult Analyze(string filePath)
    {
        byte[] bytes = File.ReadAllBytes(filePath);

        bool jeet = false;
        int pJeet = FindPattern(bytes, JeetDbHash);
        if (pJeet != -1 && pJeet + 16 <= bytes.Length && bytes[pJeet + 14] == 0x03 && bytes[pJeet + 15] == 0x00)
            jeet = true;
        else if (FindPattern(bytes, JeetObjPattern) != -1)
            jeet = true;

        bool gutgash = FindPattern(bytes, GutgashObjPattern) != -1;

        bool pinkEye = false;
        int pPe = FindPattern(bytes, PinkEyeHash);
        if (pPe != -1 && pPe + 18 <= bytes.Length)
        {
            if (bytes[pPe + 14] == 0x03 && bytes[pPe + 15] == 0x00 &&
                bytes[pPe + 16] == 0x01 && bytes[pPe + 17] == 0x00)
                pinkEye = true;
        }

        bool deepFriah = false;
        int pDf = FindPattern(bytes, DeepFriahHash);
        if (pDf != -1 && pDf + 16 <= bytes.Length)
        {
            bool header = bytes[pDf + 4] == 0x01 && bytes[pDf + 5] == 0x00 &&
                          bytes[pDf + 6] == 0x00 && bytes[pDf + 7] == 0x00;
            bool parts = bytes[pDf + 12] == 0x01 && bytes[pDf + 13] == 0x00 &&
                         bytes[pDf + 14] == 0x01 && bytes[pDf + 15] == 0x00;
            if (header && parts) deepFriah = true;
        }

        int scrapCrews = (jeet ? 1 : 0) + (gutgash ? 1 : 0) + (pinkEye ? 1 : 0) + (deepFriah ? 1 : 0);

        ChallengeInfo pennySaved = ParseChallenge(bytes, PennySavedHash, "A Penny Saved", 500);
        ChallengeInfo dividend = ParseChallenge(bytes, DividendHash, "Dividend", 2000);

        return new SaveAnalysisResult(
            filePath,
            Path.GetFileName(filePath),
            scrapCrews,
            jeet,
            gutgash,
            pinkEye,
            deepFriah,
            pennySaved,
            dividend
        );
    }

    private static ChallengeInfo ParseChallenge(byte[] bytes, byte[] idHash, string name, int defaultTarget)
    {
        int pos = FindPattern(bytes, idHash);
        if (pos == -1 || pos < 4 || pos + 16 > bytes.Length)
        {
            return new ChallengeInfo(name, false, 0, defaultTarget);
        }

        bool completed = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos - 4, 4)) == 1;
        int currentVal = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 4, 4));
        int targetVal = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 12, 4));

        if (targetVal <= 0) targetVal = defaultTarget;

        return new ChallengeInfo(name, completed, currentVal, targetVal);
    }

    private static int FindPattern(byte[] src, byte[] pattern)
    {
        int limit = src.Length - pattern.Length + 1;
        for (int i = 0; i < limit; i++)
        {
            if (src[i] != pattern[0]) continue;
            bool match = true;
            for (int j = 1; j < pattern.Length; j++)
            {
                if (src[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }
}