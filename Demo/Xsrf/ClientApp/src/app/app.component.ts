import { AfterViewInit, Component, ViewChild, importProvidersFrom } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterOutlet } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { BsNavbarComponent, BsNavbarModule } from '@mintplayer/ng-bootstrap/navbar';
import { NavMenuComponent } from './components/nav-menu/nav-menu.component';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [
    CommonModule,
    RouterOutlet,
    FormsModule,
    BsNavbarModule,
    NavMenuComponent,
  ],
  templateUrl: './app.component.html'
})
export class AppComponent implements AfterViewInit {
  title = 'ClientApp';
  nav?: BsNavbarComponent;
  @ViewChild('menu') menu?: NavMenuComponent;

  ngAfterViewInit() {
    setTimeout(() => this.nav = this.menu?.nav, 5);
    console.log('hello');
  }
}
