package app.nina.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.createSavedStateHandle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import androidx.navigation.NavType
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import androidx.navigation.navArgument
import app.nina.di.AppContainer
import app.nina.domain.model.SessionState
import app.nina.domain.model.SignedOutReason
import app.nina.ui.auth.AuthScreen
import app.nina.ui.auth.AuthViewModel
import app.nina.ui.auth.VerifyEmailScreen
import app.nina.ui.auth.VerifyEmailViewModel
import app.nina.ui.babies.BabiesScreen
import app.nina.ui.babies.BabiesViewModel
import app.nina.ui.babies.BabyFormScreen
import app.nina.ui.babies.BabyFormViewModel
import app.nina.ui.caregivers.CaregiversScreen
import app.nina.ui.caregivers.CaregiversViewModel
import app.nina.ui.invite.InviteAcceptScreen
import app.nina.ui.invite.InviteAcceptViewModel
import app.nina.ui.navigation.Routes
import app.nina.ui.onboarding.OnboardingScreen

@Composable
fun NinaApp(container: AppContainer) {
    val navController = rememberNavController()
    val session by container.authRepository.sessionState.collectAsStateWithLifecycle()
    val backStack by navController.currentBackStackEntryAsState()

    // Destino inicial calculado uma vez.
    val start = remember {
        when {
            container.authRepository.sessionState.value is SessionState.SignedIn -> Routes.BABIES
            container.appPrefs.onboardingDone -> Routes.AUTH
            else -> Routes.ONBOARDING
        }
    }

    // Reage à mudança de sessão: login abre a lista; sessão perdida volta ao login.
    LaunchedEffect(session, backStack?.destination?.route) {
        val route = backStack?.destination?.route ?: return@LaunchedEffect
        when (session) {
            is SessionState.SignedIn -> if (Routes.isPublic(route)) {
                navController.navigate(Routes.BABIES) { popUpTo(0) { inclusive = true } }
            }
            is SessionState.SignedOut -> if (!Routes.isPublic(route)) {
                navController.navigate(Routes.AUTH) { popUpTo(0) { inclusive = true } }
            }
        }
    }

    NavHost(navController = navController, startDestination = start) {
        composable(Routes.ONBOARDING) {
            OnboardingScreen(
                onStart = {
                    container.appPrefs.onboardingDone = true
                    navController.navigate(Routes.AUTH) { popUpTo(Routes.ONBOARDING) { inclusive = true } }
                },
                onHaveAccount = {
                    container.appPrefs.onboardingDone = true
                    navController.navigate(Routes.AUTH) { popUpTo(Routes.ONBOARDING) { inclusive = true } }
                },
            )
        }
        composable(Routes.AUTH) {
            val vm: AuthViewModel = viewModel(
                factory = viewModelFactory {
                    initializer { AuthViewModel(container.authRepository, container.socialAuthProvider) }
                },
            )
            val expired = (session as? SessionState.SignedOut)?.reason == SignedOutReason.EXPIRED
            AuthScreen(
                viewModel = vm,
                sessionExpired = expired,
                onBack = null,
                onVerificationRequired = { email, resend -> navController.navigate(Routes.verify(email, resend)) },
            )
        }
        composable(
            Routes.VERIFY,
            arguments = listOf(
                navArgument("email") { type = NavType.StringType },
                navArgument("resend") { type = NavType.IntType; defaultValue = -1 },
            ),
        ) { entry ->
            val email = entry.arguments?.getString("email").orEmpty()
            val resend = entry.arguments?.getInt("resend")?.takeIf { it >= 0 }
            val vm: VerifyEmailViewModel = viewModel(
                factory = viewModelFactory {
                    initializer { VerifyEmailViewModel(email, resend, container.authRepository) }
                },
            )
            VerifyEmailScreen(vm, onBack = { navController.popBackStack() })
        }
        composable(Routes.BABIES) {
            val vm: BabiesViewModel = viewModel(
                factory = viewModelFactory {
                    initializer { BabiesViewModel(container.babyRepository, container.authRepository) }
                },
            )
            BabiesScreen(
                viewModel = vm,
                onAddBaby = { navController.navigate(Routes.BABY_NEW) },
                onOpenBaby = { navController.navigate(Routes.baby(it)) },
                onCaregivers = { navController.navigate(Routes.caregivers(it)) },
                onAcceptInvite = { navController.navigate(Routes.ACCEPT_INVITE) },
            )
        }
        composable(Routes.BABY_NEW) {
            val vm: BabyFormViewModel = viewModel(
                factory = viewModelFactory {
                    initializer { BabyFormViewModel(null, container.babyRepository, container.authRepository) }
                },
            )
            BabyFormScreen(
                viewModel = vm,
                onBack = { navController.popBackStack() },
                onCreated = { id ->
                    navController.navigate(Routes.baby(id)) { popUpTo(Routes.BABY_NEW) { inclusive = true } }
                },
            )
        }
        composable(Routes.BABY, arguments = listOf(navArgument("babyId") { type = NavType.StringType })) {
            val vm: BabyFormViewModel = viewModel(
                factory = viewModelFactory {
                    initializer {
                        val id = createSavedStateHandle().get<String>("babyId").orEmpty()
                        BabyFormViewModel(id, container.babyRepository, container.authRepository)
                    }
                },
            )
            BabyFormScreen(viewModel = vm, onBack = { navController.popBackStack() }, onCreated = {})
        }
        composable(Routes.CAREGIVERS, arguments = listOf(navArgument("babyId") { type = NavType.StringType })) {
            val vm: CaregiversViewModel = viewModel(
                factory = viewModelFactory {
                    initializer {
                        val id = createSavedStateHandle().get<String>("babyId").orEmpty()
                        CaregiversViewModel(id, container.caregiverRepository, container.babyRepository, container.authRepository)
                    }
                },
            )
            CaregiversScreen(
                viewModel = vm,
                onBack = { navController.popBackStack() },
                onLeftBaby = { navController.popBackStack(Routes.BABIES, inclusive = false) },
            )
        }
        composable(Routes.ACCEPT_INVITE) {
            val vm: InviteAcceptViewModel = viewModel(
                factory = viewModelFactory {
                    initializer { InviteAcceptViewModel(container.caregiverRepository, container.babyRepository) }
                },
            )
            InviteAcceptScreen(vm, onBack = { navController.popBackStack() }, onDone = { navController.popBackStack(Routes.BABIES, inclusive = false) })
        }
    }
}
