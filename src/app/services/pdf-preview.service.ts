import { Injectable } from '@angular/core';
import { getDocument, GlobalWorkerOptions, type PDFDocumentProxy, type RenderTask } from 'pdfjs-dist';

GlobalWorkerOptions.workerSrc = '/assets/pdf.worker.min.mjs';

export function pdfDocumentUrl(file: string): string {
  if (/^\/api\/documents\/[a-zA-Z0-9_-]+$/.test(file)) return file;
  if (/^[a-zA-Z0-9_.-]+\.pdf$/i.test(file)) return `Data/${file}`;
  throw new Error('Ongeldig PDF-documentpad.');
}

@Injectable({ providedIn: 'root' })
export class PdfPreviewService {
  private readonly documents = new Map<string, Promise<PDFDocumentProxy>>();
  private readonly renderTasks = new Map<HTMLCanvasElement, RenderTask>();
  private renderVersions = new WeakMap<HTMLCanvasElement, number>();

  loadDocument(file: string): Promise<PDFDocumentProxy> {
    let documentPromise = this.documents.get(file);
    if (!documentPromise) {
      documentPromise = getDocument({
        url: pdfDocumentUrl(file), withCredentials: true, wasmUrl: '/assets/pdf-wasm/', stopAtErrors: true,
      }).promise.catch((error: unknown) => {
        this.documents.delete(file);
        throw error;
      });
      this.documents.set(file, documentPromise);
    }
    return documentPromise;
  }

  async renderPage(file: string, pageNumber: number, canvas: HTMLCanvasElement): Promise<boolean> {
    const version = (this.renderVersions.get(canvas) ?? 0) + 1;
    this.renderVersions.set(canvas, version);
    this.renderTasks.get(canvas)?.cancel();
    this.renderTasks.delete(canvas);

    try {
      const document = await this.loadDocument(file);
      const page = await document.getPage(pageNumber);
      if (this.renderVersions.get(canvas) !== version) return false;

      const baseViewport = page.getViewport({ scale: 1 });
      const availableWidth = canvas.parentElement?.clientWidth ?? baseViewport.width;
      const viewport = page.getViewport({ scale: Math.min(availableWidth / baseViewport.width, 1.4) });
      const pixelRatio = Math.min(window.devicePixelRatio || 1, 2);
      canvas.width = Math.ceil(viewport.width * pixelRatio);
      canvas.height = Math.ceil(viewport.height * pixelRatio);
      canvas.style.width = `${viewport.width}px`;
      canvas.style.height = `${viewport.height}px`;

      const task = page.render({ canvas, viewport, transform: [pixelRatio, 0, 0, pixelRatio, 0, 0] });
      this.renderTasks.set(canvas, task);
      await task.promise;
      return this.renderVersions.get(canvas) === version;
    } catch (error) {
      if (this.renderVersions.get(canvas) !== version) return false;
      if (error instanceof Error && error.name === 'RenderingCancelledException') return false;
      throw error;
    } finally {
      if (this.renderVersions.get(canvas) === version) this.renderTasks.delete(canvas);
    }
  }

  cancelRender(canvas: HTMLCanvasElement): void {
    this.renderVersions.set(canvas, (this.renderVersions.get(canvas) ?? 0) + 1);
    this.renderTasks.get(canvas)?.cancel();
    this.renderTasks.delete(canvas);
  }
}
