import SwiftUI
import NinaCore

/// E1 Boas-vindas (ux-spec 4.1): valor em uma frase, "Começar" e "Já tenho conta".
struct OnboardingView: View {
    let onStart: () -> Void
    let onHaveAccount: () -> Void

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: NinaMetrics.space6) {
                Image(systemName: "moon.stars.fill")
                    .font(.system(size: 56))
                    .foregroundStyle(Color(.eventSleep))
                    .accessibilityHidden(true)
                    .padding(.top, NinaMetrics.space8)

                VStack(alignment: .leading, spacing: NinaMetrics.space3) {
                    Text("onboarding.title")
                        .font(.ninaTitle1)
                        .foregroundStyle(Color(.textPrimary))
                        .accessibilityAddTraits(.isHeader)
                    Text("onboarding.subtitle")
                        .font(.ninaBody)
                        .foregroundStyle(Color(.textSecondary))
                        .fixedSize(horizontal: false, vertical: true)
                }

                VStack(alignment: .leading, spacing: NinaMetrics.space4) {
                    OnboardingPoint(symbol: "chart.line.uptrend.xyaxis", title: "onboarding.point1.title", detail: "onboarding.point1.detail")
                    OnboardingPoint(symbol: "person.2.fill", title: "onboarding.point2.title", detail: "onboarding.point2.detail")
                    OnboardingPoint(symbol: "lock.shield.fill", title: "onboarding.point3.title", detail: "onboarding.point3.detail")
                }
                .ninaCard()
            }
            .padding(.horizontal, NinaMetrics.gutter)
        }
        .safeAreaInset(edge: .bottom) {
            VStack(spacing: NinaMetrics.space2) {
                Button("onboarding.start", action: onStart)
                    .buttonStyle(.nina(.primary))
                Button("onboarding.have_account", action: onHaveAccount)
                    .buttonStyle(.nina(.text))
            }
            .padding(.horizontal, NinaMetrics.gutter)
            .padding(.vertical, NinaMetrics.space3)
            .background(Color(.bgApp))
        }
    }
}

private struct OnboardingPoint: View {
    let symbol: String
    let title: LocalizedStringKey
    let detail: LocalizedStringKey

    var body: some View {
        HStack(alignment: .top, spacing: NinaMetrics.space3) {
            Image(systemName: symbol)
                .font(.title3)
                .foregroundStyle(Color(.accentPrimary))
                .frame(width: 32)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: NinaMetrics.space1) {
                Text(title).font(.ninaBodyStrong).foregroundStyle(Color(.textPrimary))
                Text(detail).font(.ninaCallout).foregroundStyle(Color(.textSecondary))
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .accessibilityElement(children: .combine)
    }
}
