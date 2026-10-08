import Foundation
import Network
import Observation

/// Conectividade do aparelho (NWPathMonitor). Só alimenta o indicador de sync: o app **nunca** bloqueia o
/// registro por falta de rede (UX princípio 6); a escrita é sempre local primeiro.
@Observable
@MainActor
final class NetworkMonitor {
    private(set) var isOnline = true

    @ObservationIgnored private let monitor = NWPathMonitor()
    @ObservationIgnored private let queue = DispatchQueue(label: "app.nina.network-monitor")

    init() {
        monitor.pathUpdateHandler = { [weak self] path in
            let online = path.status == .satisfied
            Task { @MainActor in self?.isOnline = online }
        }
        monitor.start(queue: queue)
    }

    deinit { monitor.cancel() }
}
