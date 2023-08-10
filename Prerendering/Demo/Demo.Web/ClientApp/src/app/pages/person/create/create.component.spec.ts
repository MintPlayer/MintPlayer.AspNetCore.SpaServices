import { ComponentFixture, TestBed } from '@angular/core/testing';

import { PersonCreateComponent } from './create.component';

describe('PersonCreateComponent', () => {
  let component: PersonCreateComponent;
  let fixture: ComponentFixture<PersonCreateComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      declarations: [ PersonCreateComponent ]
    })
    .compileComponents();

    fixture = TestBed.createComponent(PersonCreateComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
