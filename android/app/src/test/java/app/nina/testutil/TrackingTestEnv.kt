package app.nina.testutil

import android.content.Context
import androidx.room.Room
import app.nina.data.local.BabyEntity
import app.nina.data.local.NinaDatabase
import app.nina.data.repository.DefaultTrackingRepository
import app.nina.data.repository.RoomMutationQueue
import app.nina.domain.model.Role
import app.nina.domain.tracking.EventValidator
import app.nina.domain.tracking.TrackingLimits
import java.time.Clock
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.ZoneOffset

/** Relógio controlável (injetado em repositórios, validador e ViewModels). */
class TestClock(var now: Instant = Instant.parse("2026-10-08T12:00:00Z"), private val zone: ZoneId = ZoneOffset.UTC) : Clock() {
    override fun getZone(): ZoneId = zone
    override fun withZone(zone: ZoneId): Clock = TestClock(now, zone)
    override fun instant(): Instant = now
    fun advance(d: Duration) { now = now.plus(d) }
}

const val BABY_ID = "7d0a5c9e-1c0b-4d3a-b5f4-2f9d0e6a8b21"
const val DEVICE_ID = "11111111-2222-4333-8444-555555555555"

fun babyEntity(id: String = BABY_ID, role: Role = Role.OWNER, tz: String = "America/Sao_Paulo") = BabyEntity(
    id = id, displayName = "Lia", birthDate = "2026-05-01", dueDate = null, sex = null, timezone = tz, myRole = role.name,
    version = 1, chronologicalDays = 160, correctedDays = null, correctionApplied = false, ageAsOf = "2026-10-08",
    createdAt = "2026-05-01T00:00:00Z", updatedAt = "2026-05-01T00:00:00Z",
)

/** Ambiente com Room **em memória** (Robolectric): repositório real + fila real, UUIDs sequenciais e relógio fixo. */
class TrackingEnv(context: Context, val clock: TestClock = TestClock(), limits: TrackingLimits = TrackingLimits()) {
    val db: NinaDatabase = Room.inMemoryDatabaseBuilder(context, NinaDatabase::class.java).allowMainThreadQueries().build()
    private var counter = 0
    val writes = mutableListOf<Unit>()

    fun nextId(): String = "00000000-0000-4000-8000-%012d".format(++counter)

    val repo = DefaultTrackingRepository(
        database = db, dao = db.trackingDao(), queue = db.mutationDao(), babies = db.babyDao(),
        clock = clock, deviceId = { DEVICE_ID }, validator = EventValidator(clock, limits), newId = ::nextId,
        onLocalWrite = { writes += Unit },
    )
    val queue = RoomMutationQueue(db, db.mutationDao(), db.trackingDao())

    suspend fun seedBaby(role: Role = Role.OWNER, tz: String = "America/Sao_Paulo") = db.babyDao().upsert(babyEntity(role = role, tz = tz))

    fun close() = db.close()
}
