import { Component, signal, viewChild, ChangeDetectionStrategy } from '@angular/core';
import { RouterModule } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsNavbarComponent, BsNavbarModule } from '@mintplayer/ng-bootstrap/navbar';

@Component({
  selector: 'app-nav-menu',
  templateUrl: './nav-menu.component.html',
  styleUrls: ['./nav-menu.component.css'],
  imports: [RouterModule, BsNavbarModule],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class NavMenuComponent {
  colors = Color;
  isExpanded = signal(false);
  nav = viewChild<BsNavbarComponent>('nav');

  collapse() {
    this.isExpanded.set(false);
  }

  toggle() {
    this.isExpanded.update(expanded => !expanded);
  }
}
