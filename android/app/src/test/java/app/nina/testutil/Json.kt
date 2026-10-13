package app.nina.testutil

/** JSON de exemplo no formato do contrato v1 (snake_case, enums em MAIÚSCULAS). */
object Samples {
    fun user(id: String = "u-1", email: String = "ana@example.org") = """
        {"id":"$id","email":"$email","email_verified":true,"display_name":"Ana","locale":"pt-BR",
         "timezone":"America/Sao_Paulo","status":"ACTIVE","has_password":true,"identities":[],
         "created_at":"2026-10-08T17:00:00Z","campo_novo":"ignorado"}
    """.trimIndent()

    fun tokens(access: String = "access-1", refresh: String = "refresh-1") = """
        {"token_type":"Bearer","access_token":"$access","expires_in":900,"refresh_token":"$refresh",
         "refresh_expires_at":"2026-11-07T18:00:00Z","session_id":"4a0d6c1e-2b3f-4c5d-8e9f-0a1b2c3d4e5f",
         "user":${user()}}
    """.trimIndent()

    fun baby(
        id: String = "b-1",
        name: String = "Nina",
        role: String = "OWNER",
        version: Int = 3,
        dueDate: String? = "2026-01-24",
        corrected: String = "257",
    ) = """
        {"id":"$id","display_name":"$name","birth_date":"2026-01-10",
         "due_date":${dueDate?.let { "\"$it\"" } ?: "null"},"sex":null,"timezone":"America/Sao_Paulo","photo_ref":null,
         "my_role":"$role",
         "age":{"as_of":"2026-10-08","chronological":{"days":271,"weeks":38,"months":8},
                "corrected":{"days":257,"weeks":36,"months":8},"displayed":"CHRONOLOGICAL"},
         "age_calculation":{"chronological_days":271,"corrected_days":$corrected,"correction_applied":${corrected != "null"}},
         "version":$version,"created_at":"2026-10-08T17:00:00Z","updated_at":"2026-10-08T17:10:00Z"}
    """.trimIndent()

    fun membership(
        id: String = "m-1",
        babyId: String = "b-1",
        userId: String? = "u-2",
        name: String? = "Vovó",
        email: String? = null,
        role: String = "CAREGIVER",
        status: String = "ACTIVE",
    ) = """
        {"id":"$id","baby_id":"$babyId",
         "user":${if (userId == null) "null" else """{"id":"$userId","display_name":${name?.let { "\"$it\"" } ?: "null"}}"""},
         "invited_email":${email?.let { "\"$it\"" } ?: "null"},"role":"$role","status":"$status",
         "invited_at":"2026-10-08T17:00:00Z","invitation_expires_at":"2026-10-15T17:00:00Z","accepted_at":null}
    """.trimIndent()

    fun problem(status: Int, code: String, errors: String = "[]") = """
        {"type":"https://api.nina.app/problems/x","title":"t","status":$status,"code":"$code",
         "request_id":"01J9Z3Q8M2T6V4X1B7N5C0D8EF","errors":$errors}
    """.trimIndent()
}
