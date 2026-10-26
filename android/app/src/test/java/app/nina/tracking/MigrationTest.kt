package app.nina.tracking

import androidx.room.Room
import androidx.sqlite.db.SupportSQLiteOpenHelper
import androidx.sqlite.db.framework.FrameworkSQLiteOpenHelperFactory
import androidx.test.core.app.ApplicationProvider
import app.nina.data.local.MIGRATION_1_2
import app.nina.data.local.NinaDatabase
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/** v1 -> v2 preserva o cache e cria as tabelas novas; o Room valida o esquema resultante ao abrir. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34], application = android.app.Application::class)
class MigrationTest {
    @Test fun `migracao 1 para 2 mantem bebes e habilita tracking`() = runBlocking {
        val context = ApplicationProvider.getApplicationContext<android.content.Context>()
        val callback = object : SupportSQLiteOpenHelper.Callback(1) {
            override fun onCreate(db: androidx.sqlite.db.SupportSQLiteDatabase) {
                db.execSQL("CREATE TABLE IF NOT EXISTS `baby` (`id` TEXT NOT NULL, `displayName` TEXT NOT NULL, `birthDate` TEXT NOT NULL, `dueDate` TEXT, `sex` TEXT, `timezone` TEXT NOT NULL, `myRole` TEXT NOT NULL, `version` INTEGER NOT NULL, `chronologicalDays` INTEGER, `correctedDays` INTEGER, `correctionApplied` INTEGER, `ageAsOf` TEXT, `createdAt` TEXT NOT NULL, `updatedAt` TEXT NOT NULL, PRIMARY KEY(`id`))")
                db.execSQL("CREATE TABLE IF NOT EXISTS `membership` (`id` TEXT NOT NULL, `babyId` TEXT NOT NULL, `userId` TEXT, `userDisplayName` TEXT, `invitedEmail` TEXT, `role` TEXT NOT NULL, `status` TEXT NOT NULL, `invitedAt` TEXT NOT NULL, `invitationExpiresAt` TEXT, `acceptedAt` TEXT, PRIMARY KEY(`id`))")
                db.execSQL("CREATE INDEX IF NOT EXISTS `index_membership_babyId` ON `membership` (`babyId`)")
                db.execSQL("INSERT INTO baby VALUES ('b1','Lia','2026-05-01',NULL,NULL,'UTC','OWNER',1,1,NULL,0,NULL,'x','x')")
            }
            override fun onUpgrade(db: androidx.sqlite.db.SupportSQLiteDatabase, oldVersion: Int, newVersion: Int) = Unit
        }
        FrameworkSQLiteOpenHelperFactory()
            .create(SupportSQLiteOpenHelper.Configuration.builder(context).name("mig.db").callback(callback).build())
            .writableDatabase.close()

        val db = Room.databaseBuilder(context, NinaDatabase::class.java, "mig.db").addMigrations(MIGRATION_1_2).allowMainThreadQueries().build()
        assertEquals("Lia", db.babyDao().get("b1")!!.displayName)
        assertEquals(0, db.mutationDao().all().size)
        db.close()
    }
}
