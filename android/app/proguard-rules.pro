# kotlinx.serialization: mantém serializers gerados dos DTOs.
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.AnnotationsKt
-keepclassmembers class app.nina.** {
    *** Companion;
}
-keepclasseswithmembers class app.nina.** {
    kotlinx.serialization.KSerializer serializer(...);
}
# Retrofit: interfaces de serviço.
-keep,allowobfuscation interface app.nina.data.remote.NinaApi
-keepattributes Signature, Exceptions
