import * as fs from 'fs';
import * as path from 'path';
import * as ts from 'typescript';

/**
 * Returns the relative module specifier (e.g. `./components/machines/machine-detail.component`)
 * of every component a route in `app.routes.ts` loads directly, whether through the eager
 * `component:` property or a lazy `loadComponent: () => import('...')`.
 */
export function listRoutedPageComponentModules(routesSource: string): string[] {
  const sourceFile = ts.createSourceFile('app.routes.ts', routesSource, ts.ScriptTarget.Latest, true);
  const eagerlyImportedModulesByLocalName = new Map<string, string>();
  const modules = new Set<string>();

  const visit = (node: ts.Node): void => {
    if (
      ts.isImportDeclaration(node) &&
      ts.isStringLiteral(node.moduleSpecifier) &&
      node.importClause?.namedBindings &&
      ts.isNamedImports(node.importClause.namedBindings)
    ) {
      for (const element of node.importClause.namedBindings.elements) {
        eagerlyImportedModulesByLocalName.set(element.name.text, node.moduleSpecifier.text);
      }
    }

    if (ts.isCallExpression(node) && node.expression.kind === ts.SyntaxKind.ImportKeyword) {
      const [specifier] = node.arguments;
      if (specifier && ts.isStringLiteral(specifier)) {
        modules.add(specifier.text);
      }
    }

    if (
      ts.isPropertyAssignment(node) &&
      ts.isIdentifier(node.name) &&
      node.name.text === 'component' &&
      ts.isIdentifier(node.initializer)
    ) {
      const eagerModule = eagerlyImportedModulesByLocalName.get(node.initializer.text);
      if (eagerModule) {
        modules.add(eagerModule);
      }
    }

    ts.forEachChild(node, visit);
  };

  visit(sourceFile);
  return [...modules];
}

/**
 * Reads the rendered template source of a standalone component's `@Component` decorator,
 * following `templateUrl` to disk when the template is not inline.
 */
export function resolveComponentTemplateSource(componentTsSource: string, componentDir: string): string {
  const sourceFile = ts.createSourceFile('component.ts', componentTsSource, ts.ScriptTarget.Latest, true);
  let templateSource: string | undefined;

  const visit = (node: ts.Node): void => {
    if (
      ts.isDecorator(node) &&
      ts.isCallExpression(node.expression) &&
      ts.isIdentifier(node.expression.expression) &&
      node.expression.expression.text === 'Component'
    ) {
      const [configArgument] = node.expression.arguments;
      if (configArgument && ts.isObjectLiteralExpression(configArgument)) {
        for (const property of configArgument.properties) {
          if (!ts.isPropertyAssignment(property) || !ts.isIdentifier(property.name)) {
            continue;
          }
          if (property.name.text === 'templateUrl' && ts.isStringLiteralLike(property.initializer)) {
            const templatePath = path.resolve(componentDir, property.initializer.text);
            templateSource = fs.readFileSync(templatePath, 'utf8');
          } else if (property.name.text === 'template' && ts.isStringLiteralLike(property.initializer)) {
            templateSource = property.initializer.text;
          }
        }
      }
    }
    ts.forEachChild(node, visit);
  };

  visit(sourceFile);

  if (templateSource === undefined) {
    throw new Error('Component has neither `template` nor `templateUrl`.');
  }
  return templateSource;
}

const INLINE_DIALOG_MARKUP_PATTERN = /role\s*=\s*["']dialog["']/i;

/**
 * A page/detail component's own template authoring a `role="dialog"` element is the concrete
 * signal this guard enforces: a distinct workflow with substantial UI and its own open/close
 * state that belongs in a dedicated feature component (see docs/architecture.md, "Frontend
 * architecture: page composition"), composed back in through inputs/outputs, rather than grown
 * inline on the page. A page that composes such a workflow through a child component's selector
 * (e.g. `<app-machine-restock-sync>`) never matches, because the dialog markup lives in that
 * child component's own template, not the page's.
 */
export function hasInlinePageDialogMarkup(templateSource: string): boolean {
  return INLINE_DIALOG_MARKUP_PATTERN.test(templateSource);
}
