package app.nina

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import app.nina.ui.NinaApp
import app.nina.ui.theme.NinaTheme

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        val container = (application as NinaApplication).container
        setContent {
            NinaTheme {
                NinaApp(container)
            }
        }
    }
}
