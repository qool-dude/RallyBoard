using Microsoft.EntityFrameworkCore;

namespace RallyBoard.Data;

public static class DatabaseInitializer
{
    public static void EnsureSchema(RallyBoardDbContext db)
    {
        db.Database.EnsureCreated();

        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "Sessions" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Date" date NOT NULL,
                "StartedAt" timestamp with time zone NOT NULL,
                "EndedAt" timestamp with time zone NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_Sessions_Date" ON "Sessions" ("Date");

            CREATE TABLE IF NOT EXISTS "SessionAttendances" (
                "Id" serial PRIMARY KEY,
                "SessionId" uuid NOT NULL REFERENCES "Sessions"("Id") ON DELETE CASCADE,
                "PlayerId" uuid NOT NULL REFERENCES "Players"("Id") ON DELETE RESTRICT,
                "CheckedInAt" timestamp with time zone NOT NULL,
                "HasPaid" boolean NOT NULL DEFAULT false
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_SessionAttendances_SessionId_PlayerId"
                ON "SessionAttendances" ("SessionId", "PlayerId");

            CREATE TABLE IF NOT EXISTS "Games" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "SessionId" uuid NOT NULL REFERENCES "Sessions"("Id") ON DELETE CASCADE,
                "CourtId" integer NOT NULL,
                "EndedAt" timestamp with time zone NOT NULL,
                "WinnerSide" text NOT NULL,
                "TeamAScore" integer NULL,
                "TeamBScore" integer NULL,
                "DurationSeconds" integer NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_Games_SessionId" ON "Games" ("SessionId");

            CREATE TABLE IF NOT EXISTS "GamePlayers" (
                "Id" serial PRIMARY KEY,
                "GameId" uuid NOT NULL REFERENCES "Games"("Id") ON DELETE CASCADE,
                "PlayerId" uuid NOT NULL REFERENCES "Players"("Id") ON DELETE RESTRICT,
                "TeamSide" text NOT NULL,
                "SlotIndex" integer NOT NULL
            );

            ALTER TABLE "Sessions" ADD COLUMN IF NOT EXISTS "Name" text NOT NULL DEFAULT '';

            -- Existing rows become Test; new inserts default to Live unless set explicitly.
            ALTER TABLE "Sessions" ADD COLUMN IF NOT EXISTS "IsTest" boolean NOT NULL DEFAULT true;
            ALTER TABLE "Sessions" ALTER COLUMN "IsTest" SET DEFAULT false;
            ALTER TABLE "Players" ADD COLUMN IF NOT EXISTS "IsTest" boolean NOT NULL DEFAULT true;
            ALTER TABLE "Players" ALTER COLUMN "IsTest" SET DEFAULT false;

            CREATE TABLE IF NOT EXISTS "MatchmakingExplanations" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "SessionId" uuid NOT NULL REFERENCES "Sessions"("Id") ON DELETE CASCADE,
                "GameId" uuid NULL REFERENCES "Games"("Id") ON DELETE SET NULL,
                "CourtId" integer NOT NULL,
                "PickedAt" timestamp with time zone NOT NULL,
                "WaitingPoolSize" integer NOT NULL,
                "CandidatesConsidered" integer NOT NULL,
                "RankAmongCandidates" integer NOT NULL,
                "UsedRandomness" boolean NOT NULL DEFAULT false,
                "TotalScore" double precision NOT NULL,
                "WaitingScore" double precision NOT NULL,
                "MixingScore" double precision NOT NULL,
                "BalanceScore" double precision NOT NULL,
                "PeerScore" double precision NOT NULL,
                "HomogeneityScore" double precision NOT NULL DEFAULT 0,
                "WaitingWeight" double precision NOT NULL,
                "MixingWeight" double precision NOT NULL,
                "BalanceWeight" double precision NOT NULL,
                "PeerWeight" double precision NOT NULL,
                "HomogeneityWeight" double precision NOT NULL DEFAULT 0,
                "Algorithm" text NOT NULL DEFAULT '',
                "DominantFactor" text NOT NULL DEFAULT '',
                "Summary" text NOT NULL DEFAULT '',
                "DetailsJson" text NOT NULL DEFAULT '{{}}'
            );
            CREATE INDEX IF NOT EXISTS "IX_MatchmakingExplanations_SessionId" ON "MatchmakingExplanations" ("SessionId");
            CREATE INDEX IF NOT EXISTS "IX_MatchmakingExplanations_GameId" ON "MatchmakingExplanations" ("GameId");

            ALTER TABLE "MatchmakingExplanations" ADD COLUMN IF NOT EXISTS "HomogeneityScore" double precision NOT NULL DEFAULT 0;
            ALTER TABLE "MatchmakingExplanations" ADD COLUMN IF NOT EXISTS "HomogeneityWeight" double precision NOT NULL DEFAULT 0;
            ALTER TABLE "MatchmakingExplanations" ADD COLUMN IF NOT EXISTS "Algorithm" text NOT NULL DEFAULT '';
            """);
    }
}
