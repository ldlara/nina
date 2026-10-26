package app.nina.data

import app.nina.data.remote.NinaJson
import app.nina.data.remote.dto.BabyCreateDto
import app.nina.data.remote.dto.BabyDto
import app.nina.data.remote.dto.MembershipDto
import app.nina.data.remote.dto.ProblemDto
import app.nina.data.remote.dto.ReferenceDataDto
import app.nina.data.remote.dto.TokenResponseDto
import app.nina.data.remote.dto.InvitationPreviewDto
import app.nina.data.remote.dto.LegalDocumentListDto
import app.nina.domain.model.ConsentPurpose
import app.nina.domain.model.DiaperType
import app.nina.domain.model.FeedingType
import app.nina.domain.model.InvitableRole
import app.nina.domain.model.MembershipStatus
import app.nina.domain.model.MilkType
import app.nina.domain.model.Role
import app.nina.domain.model.Sex
import app.nina.domain.model.UserStatus
import app.nina.testutil.Samples
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.LocalDate

/** Serialização do contrato v1: snake_case, enums MAIÚSCULOS e tolerância a valores/campos desconhecidos. */
class TolerantSerializationTest {

    private inline fun <reified T> decode(json: String): T = NinaJson.decodeFromString(json)

    @Test fun `baby do contrato e decodificado com age_calculation`() {
        val dto: BabyDto = decode(Samples.baby())
        assertEquals(Role.OWNER, dto.myRole)
        assertEquals(271, dto.ageCalculation?.chronologicalDays)
        assertEquals(257, dto.ageCalculation?.correctedDays)
        assertTrue(dto.ageCalculation!!.correctionApplied)
        assertEquals("2026-10-08", dto.age?.asOf)
    }

    @Test fun `corrected_days nulo significa nao se aplica e nao zero`() {
        val dto: BabyDto = decode(Samples.baby(dueDate = null, corrected = "null"))
        assertNull(dto.ageCalculation?.correctedDays)
        assertFalse(dto.ageCalculation!!.correctionApplied)
        assertNull(dto.dueDate)
    }

    @Test fun `papel desconhecido vira UNRECOGNIZED sem falhar nem descartar o registro`() {
        val dto: BabyDto = decode(Samples.baby(role = "GUARDIAN_ANGEL"))
        assertEquals(Role.UNRECOGNIZED, dto.myRole)
        assertEquals("Nina", dto.displayName)
    }

    @Test fun `sexo e status de membro desconhecidos sao tolerados`() {
        val baby: BabyDto = decode(Samples.baby().replace("\"sex\":null", "\"sex\":\"NONBINARY\""))
        assertEquals(Sex.UNRECOGNIZED, baby.sex)
        val member: MembershipDto = decode(Samples.membership(status = "SUSPENDED", role = "AUDITOR"))
        assertEquals(MembershipStatus.UNRECOGNIZED, member.status)
        assertEquals(Role.UNRECOGNIZED, member.role)
        assertEquals("Vovó", member.user?.displayName)
    }

    @Test fun `enums sao case sensitive - minusculas nao sao do contrato`() {
        val member: MembershipDto = decode(Samples.membership(role = "owner", status = "active"))
        assertEquals(Role.UNRECOGNIZED, member.role)
        assertEquals(MembershipStatus.UNRECOGNIZED, member.status)
    }

    @Test fun `campos desconhecidos sao ignorados`() {
        val tokens: TokenResponseDto = decode(Samples.tokens())
        assertEquals("access-1", tokens.accessToken)
        assertEquals(UserStatus.ACTIVE, tokens.user.status)
        assertEquals("Ana", tokens.user.displayName)
    }

    @Test fun `status de usuario e papel de convite desconhecidos`() {
        val user = NinaJson.decodeFromString<app.nina.data.remote.dto.UserDto>(
            Samples.user().replace("\"ACTIVE\"", "\"FROZEN\""),
        )
        assertEquals(UserStatus.UNRECOGNIZED, user.status)
        val preview: InvitationPreviewDto = decode(
            """{"inviter_display_name":"Ana","baby_label":"N.","role":"EDITOR","expires_at":"2026-10-15T17:00:00Z","novo":1}""",
        )
        assertEquals(InvitableRole.UNRECOGNIZED, preview.role)
    }

    @Test fun `enums extensiveis do reference-data toleram valores novos`() {
        val ref: ReferenceDataDto = decode(
            """
            {"version":"2026.10.1","diaper_types":["WET","DIRTY","FLUFFY"],"feeding_types":["BOTTLE","PUREE"],
             "milk_types":["FORMULA","GOAT"],"sleep_methods":["CRIB"],
             "policies":{"corrected_age_max_months":24,"deletion_grace_days":7,"politica_nova":"x"},
             "limits":{"invitation_ttl_days":7}}
            """.trimIndent(),
        )
        assertEquals(listOf(DiaperType.WET, DiaperType.DIRTY, DiaperType.UNRECOGNIZED), ref.diaperTypes)
        assertEquals(listOf(FeedingType.BOTTLE, FeedingType.UNRECOGNIZED), ref.feedingTypes)
        assertEquals(listOf(MilkType.FORMULA, MilkType.UNRECOGNIZED), ref.milkTypes)
        assertEquals(24, ref.policies.correctedAgeMaxMonths)
        assertEquals(7, ref.limits.invitationTtlDays)
    }

    @Test fun `finalidade de consentimento desconhecida nao quebra a lista de documentos`() {
        val list: LegalDocumentListDto = decode(
            """
            {"items":[
              {"purpose_key":"TERMS_OF_USE","version":"1.0.0","url":"https://example.invalid/t","required":true},
              {"purpose_key":"BRAND_NEW_PURPOSE","version":"2","url":"https://example.invalid/n"}]}
            """.trimIndent(),
        )
        assertEquals(ConsentPurpose.TERMS_OF_USE, list.items[0].purposeKey)
        assertEquals(ConsentPurpose.UNRECOGNIZED, list.items[1].purposeKey)
    }

    @Test fun `problem json e interpretado e ignora extensoes`() {
        val p: ProblemDto = decode(
            """{"type":"x","title":"t","status":409,"code":"OWNER_DECISION_REQUIRED","deletion_policy":"CASCADE",
                "errors":[{"field":"data.end_at","code":"END_BEFORE_START","meta":{"a":1}}],"request_id":"r1"}""",
        )
        assertEquals("OWNER_DECISION_REQUIRED", p.code)
        assertEquals("END_BEFORE_START", p.errors.single().code)
        assertEquals("r1", p.requestId)
    }

    @Test fun `requisicao usa snake_case enums maiusculos e omite nulos`() {
        val body = NinaJson.encodeToString(
            BabyCreateDto.serializer(),
            BabyCreateDto(id = "b-1", displayName = "Nina", birthDate = "2026-01-10", dueDate = null, sex = Sex.FEMALE, timezone = "America/Sao_Paulo"),
        )
        val obj = Json.parseToJsonElement(body).jsonObject
        assertEquals("Nina", obj.getValue("display_name").jsonPrimitive.content)
        assertEquals("2026-01-10", obj.getValue("birth_date").jsonPrimitive.content)
        assertEquals("FEMALE", obj.getValue("sex").jsonPrimitive.content)
        assertFalse("due_date" in obj)
        assertFalse(body.contains("displayName"))
    }

    @Test fun `valor so do cliente UNRECOGNIZED nunca e enviado ao servidor`() {
        assertThrows(IllegalArgumentException::class.java) {
            NinaJson.encodeToString(
                BabyCreateDto.serializer(),
                BabyCreateDto(displayName = "N", birthDate = "2026-01-10", sex = Sex.UNRECOGNIZED),
            )
        }
    }

    @Test fun `datas do bebe sao datas civis`() {
        val dto: BabyDto = decode(Samples.baby())
        assertEquals(LocalDate.of(2026, 1, 10), LocalDate.parse(dto.birthDate))
    }
}
