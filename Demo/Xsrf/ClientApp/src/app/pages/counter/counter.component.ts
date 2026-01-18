import { Component, signal, ChangeDetectionStrategy } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';

@Component({
  selector: 'app-counter-component',
  templateUrl: './counter.component.html',
  imports: [BsButtonTypeDirective],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CounterComponent {
  colors = Color;
  currentCount = signal(0);

  incrementCounter() {
    this.currentCount.update(count => count + 1);
  }
}
