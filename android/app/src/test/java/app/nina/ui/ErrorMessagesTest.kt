package app.nina.ui

import app.nina.R
import app.nina.domain.model.AppError
import app.nina.domain.model.FieldError
import org.junit.Assert.assertEquals
import org.junit.Test

class ErrorMessagesTest {
    private fun api(status: Int, code: String) = AppError.Api(status, code)

    @Test fun `mensagens sao localizadas por code e nao por titulo`() {
        assertEquals(R.string.error_invalid_credentials, api(401, "INVALID_CREDENTIALS").messageRes())
        assertEquals(R.string.error_session_expired, api(401, "SESSION_REVOKED").messageRes())
        assertEquals(R.string.error_session_expired, api(401, "REFRESH_TOKEN_REUSED").messageRes())
        assertEquals(R.string.error_access_revoked, api(403, "ACCESS_REVOKED").messageRes())
        assertEquals(R.string.error_identity_link_required, api(409, "IDENTITY_LINK_REQUIRED").messageRes())
        assertEquals(R.string.error_rate_limited, api(429, "RATE_LIMITED").messageRes())
        assertEquals(R.string.error_upgrade_required, api(426, "CLIENT_UPGRADE_REQUIRED").messageRes())
    }

    @Test fun `codigo desconhecido cai em mensagem generica ou de servidor`() {
        assertEquals(R.string.error_generic, api(409, "ALGUM_CODE_NOVO").messageRes())
        assertEquals(R.string.error_server, api(502, "HTTP_502").messageRes())
        assertEquals(R.string.error_network, AppError.Network.messageRes())
        assertEquals(R.string.error_generic, AppError.Unexpected("x").messageRes())
    }

    @Test fun `erros de campo`() {
        assertEquals(R.string.field_required, FieldError("x", "REQUIRED").messageRes())
        assertEquals(R.string.field_future_date, FieldError("x", "FUTURE_DATE").messageRes())
        assertEquals(R.string.field_invalid, FieldError("x", "NOVO").messageRes())
        val e = AppError.Api(400, "VALIDATION_FAILED", fieldErrors = listOf(FieldError("data.birth_date", "FUTURE_DATE")))
        assertEquals("FUTURE_DATE", e.fieldError("birth_date")!!.code)
    }
}
