namespace RallyBoard.Models;

public record PlayerRankingRow(
    Guid PlayerId,
    string Name,
    int Games,
    int Wins,
    int Losses,
    double WinRate,
    int PointsFor,
    int PointsAgainst,
    bool HasPaid);

public record GameSummaryRow(
    Guid Id,
    DateTime EndedAt,
    int CourtId,
    string WinnerSide,
    int? TeamAScore,
    int? TeamBScore,
    string TeamANames,
    string TeamBNames);

public record SessionSummaryRow(
    Guid Id,
    DateOnly Date,
    string Name,
    int GameCount,
    int PlayerCount,
    bool IsActive);

public record SessionStats(
    int TotalGames,
    int TotalPlayers,
    int PaidCount,
    int UnpaidCount);
