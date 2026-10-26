package app.nina.domain.model

import kotlinx.serialization.KSerializer
import kotlinx.serialization.descriptors.PrimitiveKind
import kotlinx.serialization.descriptors.PrimitiveSerialDescriptor
import kotlinx.serialization.descriptors.SerialDescriptor
import kotlinx.serialization.encoding.Decoder
import kotlinx.serialization.encoding.Encoder

/**
 * Serializer de enum tolerante (ADR-0009/0010, AD-27): valores desconhecidos em respostas viram [fallback]
 * (`UNRECOGNIZED`), sem falhar nem descartar o registro. O contrato usa nomes em MAIÚSCULAS, iguais a `name`.
 *
 * Na escrita nunca se envia [fallback]: é um valor só do cliente (o servidor responderia 400 VALIDATION_FAILED).
 */
abstract class TolerantEnumSerializer<E : Enum<E>>(
    serialName: String,
    private val values: List<E>,
    private val fallback: E,
) : KSerializer<E> {
    override val descriptor: SerialDescriptor = PrimitiveSerialDescriptor(serialName, PrimitiveKind.STRING)

    override fun deserialize(decoder: Decoder): E {
        val raw = decoder.decodeString()
        return values.firstOrNull { it.name == raw } ?: fallback
    }

    override fun serialize(encoder: Encoder, value: E) {
        require(value != fallback) { "O valor ${value.name} é só do cliente e não pode ser enviado ao servidor" }
        encoder.encodeString(value.name)
    }
}
