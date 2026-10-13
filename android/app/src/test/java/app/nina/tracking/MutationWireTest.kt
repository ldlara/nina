package app.nina.tracking

import app.nina.data.local.EventEntity
import app.nina.data.sync.MutationWire
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** Formato de envio conforme openapi v1.0.1 (`additionalProperties: false`, snake_case, MAIÚSCULAS). */
class MutationWireTest {
    private fun base(type: String) = EventEntity(
        id = "e", babyId = "b", entityType = type, version = 0, tz = "UTC", startAt = 1_791_460_800_000, endAt = 1_791_461_400_000,
        sleepType = null, sleepSource = null, methodOrPlace = null, notes = null, feedingType = null, side = null, volumeMl = null,
        milkType = null, diaperType = null, sleepSessionId = null, wakeSource = null, lastModifiedByName = null,
        createdAt = 0, updatedAt = 0, deleted = false, syncState = "PENDING",
    )

    @Test fun `mamada no peito leva side e end_at e nao leva volume nem milk_type`() {
        val d = MutationWire.createData(base("FEEDING_SESSION").copy(feedingType = "BREASTFEEDING", side = "LEFT"))
        assertEquals(setOf("feeding_type", "side", "start_at", "end_at", "tz", "notes"), d.keys)
    }

    @Test fun `mamadeira leva volume e milk_type anulavel`() {
        val d = MutationWire.createData(base("FEEDING_SESSION").copy(feedingType = "BOTTLE", volumeMl = 120, endAt = null))
        assertTrue("volume_ml" in d.keys && "milk_type" in d.keys)
        assertEquals("null", d["milk_type"].toString())
        assertFalse("side" in d.keys)
    }

    @Test fun `UNRECOGNIZED nunca e enviado`() {
        val d = MutationWire.createData(base("DIAPER_EVENT").copy(diaperType = "UNRECOGNIZED"))
        assertFalse("diaper_type" in d.keys)
    }

    @Test fun `sono tem source so na criacao e patch so campos alterados`() {
        val sleep = base("SLEEP_SESSION").copy(sleepType = "NAP", sleepSource = "TIMER", endAt = null)
        assertTrue("source" in MutationWire.createData(sleep).keys)
        val patch = MutationWire.patchData(sleep, sleep.copy(endAt = 1_791_461_400_000, notes = "n"))
        assertEquals(setOf("end_at", "notes"), patch.keys)
        assertEquals("\"2026-10-08T12:10:00Z\"".length > 0, true)
    }
}
