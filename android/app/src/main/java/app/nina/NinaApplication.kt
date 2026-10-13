package app.nina

import android.app.Application
import app.nina.di.AppContainer

class NinaApplication : Application() {
    lateinit var container: AppContainer
        private set

    override fun onCreate() {
        super.onCreate()
        container = AppContainer(this)
    }
}
