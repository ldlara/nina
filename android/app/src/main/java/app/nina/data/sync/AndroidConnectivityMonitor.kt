package app.nina.data.sync

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import app.nina.domain.tracking.ConnectivityMonitor
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** Conectividade via [ConnectivityManager]. Só informa a UI; quem decide enviar é o motor de sync. */
class AndroidConnectivityMonitor(context: Context) : ConnectivityMonitor {
    private val manager = context.applicationContext.getSystemService(ConnectivityManager::class.java)
    private val _online = MutableStateFlow(currentlyOnline())
    override val isOnline: StateFlow<Boolean> = _online.asStateFlow()

    init {
        try {
            manager?.registerDefaultNetworkCallback(object : ConnectivityManager.NetworkCallback() {
                override fun onAvailable(network: Network) { _online.value = true }
                override fun onLost(network: Network) { _online.value = currentlyOnline() }
                override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) {
                    _online.value = caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                }
            })
        } catch (_: SecurityException) {
            // Sem permissão/serviço: mantém o último valor conhecido (otimista).
        }
    }

    private fun currentlyOnline(): Boolean {
        val m = manager ?: return true
        val caps = m.getNetworkCapabilities(m.activeNetwork) ?: return false
        return caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
    }
}
