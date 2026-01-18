import { Component, inject, effect, ChangeDetectionStrategy } from '@angular/core';
import { RouterOutlet, Router, ActivatedRoute } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { TranslateService } from '@ngx-translate/core';
import { MESSAGE_TOKEN } from './tokens';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class App {
  private readonly translateService = inject(TranslateService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  readonly message = inject(MESSAGE_TOKEN);

  title = 'ClientApp';

  private readonly queryParams = toSignal(this.route.queryParamMap);

  constructor() {
    this.translateService.setDefaultLang('en');

    effect(() => {
      const params = this.queryParams();
      if (params) {
        const lang = params.get('lang');
        if (lang) {
          this.translateService.use(lang);
        }
      }
    });
  }

  useLanguage(language: string) {
    this.router.navigate([], {
      queryParams: {
        lang: language
      }
    });
  }
}
