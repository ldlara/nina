package app.nina.data.remote

import retrofit2.converter.kotlinx.serialization.asConverterFactory
import okhttp3.Authenticator
import okhttp3.Interceptor
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import retrofit2.Retrofit
import java.util.concurrent.TimeUnit

object NetworkFactory {
    fun okHttp(interceptor: Interceptor, authenticator: Authenticator? = null): OkHttpClient =
        OkHttpClient.Builder()
            .addInterceptor(interceptor)
            .apply { authenticator?.let { authenticator(it) } }
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(30, TimeUnit.SECONDS)
            .writeTimeout(30, TimeUnit.SECONDS)
            .build()

    /** [baseUrl] deve terminar em `/` e incluir `/v1/` (ex.: `https://host/v1/`). */
    fun api(baseUrl: String, client: OkHttpClient): NinaApi =
        Retrofit.Builder()
            .baseUrl(baseUrl)
            .client(client)
            .addConverterFactory(NinaJson.asConverterFactory("application/json".toMediaType()))
            .build()
            .create(NinaApi::class.java)
}
