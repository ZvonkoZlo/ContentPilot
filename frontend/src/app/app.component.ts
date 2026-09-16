import { Component } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { SettingsBarComponent } from './settings-bar/settings-bar.component';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, SettingsBarComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css',
})
export class AppComponent {
  title = 'ContentPilot review UI';
}
