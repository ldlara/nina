import Foundation
import Observation

/// Lista de bebês do usuário (Home): cache local primeiro, depois rede.
@Observable
@MainActor
public final class BabiesViewModel {
    public private(set) var babies: [Baby] = []
    public private(set) var state: LoadState = .idle
    public private(set) var isShowingCachedData = false
    public var selectedBabyId: Baby.ID?

    @ObservationIgnored private let repository: BabyRepository

    public init(repository: BabyRepository) {
        self.repository = repository
    }

    public var selectedBaby: Baby? {
        babies.first { $0.id == selectedBabyId } ?? babies.first
    }

    /// Carregado com sucesso e sem nenhum bebê: o app leva à criação do primeiro bebê (UX 4.1 E4).
    public var needsFirstBaby: Bool {
        state == .loaded && babies.isEmpty
    }

    public func load() async {
        if babies.isEmpty {
            let cached = await repository.cachedBabies()
            if !cached.isEmpty {
                babies = cached
                isShowingCachedData = true
            }
        }
        state = .loading
        do {
            let result = try await repository.refreshBabies()
            babies = result.babies
            isShowingCachedData = result.isFromCache
            state = .loaded
        } catch let error as APIError where error.problemCode == ProblemCode.accessRevoked {
            babies = []
            await repository.clearLocalData()
            state = .failed(ErrorMapper.message(for: error))
        } catch {
            // Mantém o que já está na tela (cache) e informa o erro.
            state = babies.isEmpty ? .failed(ErrorMapper.message(for: error)) : .loaded
            isShowingCachedData = !babies.isEmpty
        }
    }

    public func didSave(_ baby: Baby) {
        if let index = babies.firstIndex(where: { $0.id == baby.id }) {
            babies[index] = baby
        } else {
            babies.append(baby)
        }
        selectedBabyId = baby.id
        state = .loaded
    }

    public func didLeave(babyId: Baby.ID) {
        babies.removeAll { $0.id == babyId }
        if selectedBabyId == babyId { selectedBabyId = babies.first?.id }
    }
}
