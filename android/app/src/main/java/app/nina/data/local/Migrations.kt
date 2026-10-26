package app.nina.data.local

import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase

/**
 * v1 -> v2: registros de tracking e fila de mutações. SQL copiado de `app/schemas/.../2.json` (gerado pelo Room).
 * Não há `fallbackToDestructiveMigration`: a fila de mutações guarda dados do usuário ainda não enviados.
 */
val MIGRATION_1_2 = object : Migration(1, 2) {
    override fun migrate(db: SupportSQLiteDatabase) {
        db.execSQL(
            "CREATE TABLE IF NOT EXISTS `tracking_event` (`id` TEXT NOT NULL, `babyId` TEXT NOT NULL, `entityType` TEXT NOT NULL, " +
                "`version` INTEGER NOT NULL, `tz` TEXT NOT NULL, `startAt` INTEGER NOT NULL, `endAt` INTEGER, `sleepType` TEXT, " +
                "`sleepSource` TEXT, `methodOrPlace` TEXT, `notes` TEXT, `feedingType` TEXT, `side` TEXT, `volumeMl` INTEGER, " +
                "`milkType` TEXT, `diaperType` TEXT, `sleepSessionId` TEXT, `wakeSource` TEXT, `lastModifiedByName` TEXT, " +
                "`createdAt` INTEGER NOT NULL, `updatedAt` INTEGER NOT NULL, `deleted` INTEGER NOT NULL, `syncState` TEXT NOT NULL, " +
                "PRIMARY KEY(`id`))",
        )
        db.execSQL("CREATE INDEX IF NOT EXISTS `index_tracking_event_babyId_startAt` ON `tracking_event` (`babyId`, `startAt`)")
        db.execSQL("CREATE INDEX IF NOT EXISTS `index_tracking_event_sleepSessionId` ON `tracking_event` (`sleepSessionId`)")
        db.execSQL(
            "CREATE TABLE IF NOT EXISTS `sync_mutation` (`seq` INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, `mutationId` TEXT NOT NULL, " +
                "`babyId` TEXT NOT NULL, `entityType` TEXT NOT NULL, `entityId` TEXT NOT NULL, `op` TEXT NOT NULL, " +
                "`baseVersion` INTEGER NOT NULL, `clientCreatedAt` INTEGER NOT NULL, `deviceId` TEXT NOT NULL, `payload` TEXT, " +
                "`status` TEXT NOT NULL, `attempts` INTEGER NOT NULL, `nextAttemptAt` INTEGER NOT NULL, `lastErrorCode` TEXT)",
        )
        db.execSQL("CREATE UNIQUE INDEX IF NOT EXISTS `index_sync_mutation_mutationId` ON `sync_mutation` (`mutationId`)")
        db.execSQL("CREATE INDEX IF NOT EXISTS `index_sync_mutation_entityId` ON `sync_mutation` (`entityId`)")
        db.execSQL("CREATE INDEX IF NOT EXISTS `index_sync_mutation_status_seq` ON `sync_mutation` (`status`, `seq`)")
    }
}
