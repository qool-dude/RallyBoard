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
            """);
    }
}
