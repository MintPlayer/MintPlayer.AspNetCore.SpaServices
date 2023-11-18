import { Component, ViewChild } from '@angular/core';
import { RouterModule } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsNavbarComponent, BsNavbarModule } from '@mintplayer/ng-bootstrap/navbar';

@Component({
  selector: 'app-nav-menu',
  templateUrl: './nav-menu.component.html',
  styleUrls: ['./nav-menu.component.css'],
  standalone: true,
  imports: [RouterModule, BsNavbarModule],
})
export class NavMenuComponent {
  colors = Color;
  isExpanded = false;
  @ViewChild('nav') nav!: BsNavbarComponent;

  collapse() {
    this.isExpanded = false;
  }

  toggle() {
    this.isExpanded = !this.isExpanded;
  }
}
