import { Component, viewChild, computed, ChangeDetectionStrategy } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { BsNavbarModule } from '@mintplayer/ng-bootstrap/navbar';
import { NavMenuComponent } from './components/nav-menu/nav-menu.component';

@Component({
  selector: 'app-root',
  imports: [
    RouterOutlet,
    FormsModule,
    BsNavbarModule,
    NavMenuComponent,
  ],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class App {
  title = 'ClientApp';
  menu = viewChild<NavMenuComponent>('menu');
  nav = computed(() => this.menu()?.nav());
}
