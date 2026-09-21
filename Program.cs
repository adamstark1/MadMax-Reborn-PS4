using System;
using System.IO;
using MadMaxReborn.Services;

namespace MadMaxReborn;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("   MadMaxReborn - Backend Save Engine   ");
        Console.WriteLine("========================================\n");

        var scanner = new SaveScannerService();
        string baseDir = AppContext.BaseDirectory;

        string? savePath = scanner.FindLatestSaveFile(baseDir);

        if (savePath == null)
        {
            string parentDir = Directory.GetParent(baseDir)?.Parent?.Parent?.FullName ?? baseDir;
            savePath = scanner.FindLatestSaveFile(parentDir);
        }

        if (savePath == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[ERROR] No GAMESAVE01 - GAMESAVE10 files found beside the executable.");
            Console.ResetColor();
            return;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[FOUND] Target save file: {Path.GetFileName(savePath)}\n");
        Console.ResetColor();

        var result = scanner.Analyze(savePath);

        Console.WriteLine("--- STRONGHOLD PROJECTS ---");
        Console.WriteLine($"Total Scrap Crews Built: {result.ScrapCrewsBuilt} / 4");
        Console.WriteLine($"  - Jeet's Stronghold      : {(result.JeetBuilt ? "BUILT" : "MISSING")}");
        Console.WriteLine($"  - Gutgash's Stronghold   : {(result.GutgashBuilt ? "BUILT" : "MISSING")}");
        Console.WriteLine($"  - Pink Eye's Stronghold  : {(result.PinkEyeBuilt ? "BUILT" : "MISSING")}");
        Console.WriteLine($"  - Deep Friah's Stronghold: {(result.DeepFriahBuilt ? "BUILT" : "MISSING")}");

        Console.WriteLine("\n--- CHALLENGES ---");
        Console.WriteLine($"[{result.PennySaved.Name}]");
        Console.WriteLine($"  Status  : {(result.PennySaved.Completed ? "COMPLETED" : "IN PROGRESS")}");
        Console.WriteLine($"  Progress: {result.PennySaved.CurrentValue} / {result.PennySaved.TargetValue} Scrap");

        Console.WriteLine($"[{result.Dividend.Name}]");
        Console.WriteLine($"  Status  : {(result.Dividend.Completed ? "COMPLETED" : "IN PROGRESS")}");
        Console.WriteLine($"  Progress: {result.Dividend.CurrentValue} / {result.Dividend.TargetValue} Scrap");
    }
}