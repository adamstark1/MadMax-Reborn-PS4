namespace MadMaxReborn.Models;

public record ChallengeInfo(
    string Name,
    bool Completed,
    int CurrentValue,
    int TargetValue
);

public record SaveAnalysisResult(
    string FilePath,
    string FileName,
    int ScrapCrewsBuilt,
    bool JeetBuilt,
    bool GutgashBuilt,
    bool PinkEyeBuilt,
    bool DeepFriahBuilt,
    ChallengeInfo PennySaved,
    ChallengeInfo Dividend
);