import { Component, signal, ChangeDetectionStrategy } from '@angular/core';

@Component({
  selector: 'app-counter-component',
  templateUrl: './counter.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CounterComponent {
  currentCount = signal(0);

  incrementCounter() {
    this.currentCount.update(count => count + 1);
  }
}
