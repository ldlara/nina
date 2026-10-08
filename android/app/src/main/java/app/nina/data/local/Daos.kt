package app.nina.data.local

import androidx.room.Dao
import androidx.room.Database
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.RoomDatabase
import androidx.room.Transaction
import kotlinx.coroutines.flow.Flow

@Dao
abstract class BabyDao {
    @Query("SELECT * FROM baby ORDER BY createdAt ASC")
    abstract fun observeAll(): Flow<List<BabyEntity>>

    @Query("SELECT * FROM baby WHERE id = :id")
    abstract fun observe(id: String): Flow<BabyEntity?>

    @Query("SELECT * FROM baby WHERE id = :id")
    abstract suspend fun get(id: String): BabyEntity?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun upsert(baby: BabyEntity)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun upsertAll(babies: List<BabyEntity>)

    @Query("DELETE FROM baby")
    abstract suspend fun deleteAll()

    @Query("DELETE FROM baby WHERE id = :id")
    abstract suspend fun delete(id: String)

    /** Substitui o conjunto: bebês que o servidor não devolve mais (acesso revogado) saem do cache. */
    @Transaction
    open suspend fun replaceAll(babies: List<BabyEntity>) {
        deleteAll()
        upsertAll(babies)
    }
}

@Dao
abstract class MembershipDao {
    @Query("SELECT * FROM membership WHERE babyId = :babyId ORDER BY invitedAt ASC")
    abstract fun observe(babyId: String): Flow<List<MembershipEntity>>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun upsert(item: MembershipEntity)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun upsertAll(items: List<MembershipEntity>)

    @Query("DELETE FROM membership WHERE babyId = :babyId")
    abstract suspend fun deleteForBaby(babyId: String)

    @Query("DELETE FROM membership WHERE id = :id")
    abstract suspend fun delete(id: String)

    @Query("DELETE FROM membership")
    abstract suspend fun deleteAll()

    @Transaction
    open suspend fun replaceForBaby(babyId: String, items: List<MembershipEntity>) {
        deleteForBaby(babyId)
        upsertAll(items)
    }
}

@Database(
    entities = [BabyEntity::class, MembershipEntity::class, EventEntity::class, MutationEntity::class],
    version = 2,
    exportSchema = true,
)
abstract class NinaDatabase : RoomDatabase() {
    abstract fun babyDao(): BabyDao
    abstract fun membershipDao(): MembershipDao
    abstract fun trackingDao(): TrackingDao
    abstract fun mutationDao(): MutationDao
}
