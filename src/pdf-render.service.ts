import { inject, Injectable } from '@angular/core';
import type { RenderTask } from 'pdfjs-dist';
import { ExamService } from './exam.service';
import type { ExamQuestion } from './exam-data';

export type PdfRenderKind = 'exam' | 'answer' | 'previousAnswer';

export interface PdfRenderContext {
  setPreviewState(kind: PdfRenderKind, loading: boolean, error: string): void;
  isCurrentExam(file: string): boolean;
  isExamPageRendered(pageNumber: number): boolean;
  setExamPageRendered(pageNumber: number): void;
  setExamPageError(pageNumber: number, error: string): void;
}

@Injectable({ providedIn: 'root' })
export class PdfRenderService {
  private readonly examService = inject(ExamService);
  private readonly renderTasks = new Map<HTMLCanvasElement, RenderTask>();
  private readonly renderingExamPages = new Map<string, number>();
  private renderVersions = new WeakMap<HTMLCanvasElement, number>();
  private renderSequence = 0;

  renderAnswerPage(
    question: ExamQuestion,
    answerCanvas: HTMLCanvasElement | undefined,
    previousAnswerCanvas: HTMLCanvasElement | undefined,
    previousAnswerPage: number,
    context: PdfRenderContext,
  ): void {
    if (answerCanvas) {
      void this.renderPage(question.answerFile, question.answerPage, answerCanvas, 'answer', context);
      if (previousAnswerPage && previousAnswerCanvas) {
        void this.renderPage(question.answerFile, previousAnswerPage, previousAnswerCanvas, 'previousAnswer', context);
      }
    }
  }

  async renderPage(
    file: string,
    pageNumber: number,
    canvas: HTMLCanvasElement,
    kind: PdfRenderKind,
    context: PdfRenderContext,
  ): Promise<void> {
    const renderKey = `${file}:${pageNumber}`;
    if (kind === 'exam') {
      if (this.renderingExamPages.has(renderKey) || context.isExamPageRendered(pageNumber)) return;
      this.renderingExamPages.set(renderKey, ++this.renderSequence);
    }
    const requestVersion = this.renderingExamPages.get(renderKey);
    context.setPreviewState(kind, true, '');

    try {
      const rendered = await this.renderCanvasPage(file, pageNumber, canvas);
      if (!rendered) return;
      if (kind === 'exam') {
        if (!context.isCurrentExam(file)) return;
        context.setExamPageRendered(pageNumber);
      } else {
        context.setPreviewState(kind, false, '');
      }
    } catch (error) {
      if (error instanceof Error && error.name === 'RenderingCancelledException') return;
      const message = 'De PDF-pagina kon niet worden geladen. Probeer de vraag opnieuw.';
      if (kind === 'exam') {
        if (!context.isCurrentExam(file)) return;
        context.setExamPageError(pageNumber, message);
      } else {
        context.setPreviewState(kind, false, message);
      }
    } finally {
      if (kind === 'exam' && this.renderingExamPages.get(renderKey) === requestVersion) {
        this.renderingExamPages.delete(renderKey);
      }
    }
  }

  private async renderCanvasPage(file: string, pageNumber: number, canvas: HTMLCanvasElement): Promise<boolean> {
    const version = this.beginRender(canvas);

    try {
      const document = await this.examService.loadDocument(file);
      if (!this.isCurrentRender(canvas, version)) return false;

      const page = await document.getPage(pageNumber);
      if (!this.isCurrentRender(canvas, version)) return false;

      const baseViewport = page.getViewport({ scale: 1 });
      const availableWidth = canvas.parentElement?.clientWidth ?? baseViewport.width;
      const scale = Math.min(availableWidth / baseViewport.width, 1.4);
      const viewport = page.getViewport({ scale });
      const pixelRatio = Math.min(window.devicePixelRatio || 1, 2);
      canvas.width = Math.ceil(viewport.width * pixelRatio);
      canvas.height = Math.ceil(viewport.height * pixelRatio);
      canvas.style.width = `${viewport.width}px`;
      canvas.style.height = `${viewport.height}px`;

      const renderTask = page.render({ canvas, viewport, transform: [pixelRatio, 0, 0, pixelRatio, 0, 0] });
      this.renderTasks.set(canvas, renderTask);
      await renderTask.promise;
      return this.isCurrentRender(canvas, version);
    } catch (error) {
      if (!this.isCurrentRender(canvas, version)) return false;
      if (error instanceof Error && error.name === 'RenderingCancelledException') return false;
      throw error;
    } finally {
      if (this.isCurrentRender(canvas, version)) {
        this.renderTasks.delete(canvas);
      }
    }
  }

  cancelAll(): void {
    for (const task of this.renderTasks.values()) {
      task.cancel();
    }
    this.renderTasks.clear();
    this.renderVersions = new WeakMap<HTMLCanvasElement, number>();
    this.renderingExamPages.clear();
  }

  private beginRender(canvas: HTMLCanvasElement): number {
    const version = (this.renderVersions.get(canvas) ?? 0) + 1;
    this.renderVersions.set(canvas, version);
    this.renderTasks.get(canvas)?.cancel();
    this.renderTasks.delete(canvas);
    return version;
  }

  private isCurrentRender(canvas: HTMLCanvasElement, version: number): boolean {
    return this.renderVersions.get(canvas) === version;
  }
}
