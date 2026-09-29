using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocsDR.App.Views;
using DocsDR.Core;

namespace DocsDR.App.ViewModels;

/// <summary>Vista «Organizar páginas»: cuadrícula de miniaturas para mover, duplicar, girar, borrar e insertar páginas.</summary>
public sealed partial class DocumentViewModel
{
    public const double MinGridThumb = 90, MaxGridThumb = 320, DefaultGridThumb = 150;

    /// <summary>Ancho de las miniaturas de la cuadrícula (lo cambia el deslizador).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridCellWidth), nameof(GridCellHeight))]
    private double _gridThumbSize = DefaultGridThumb;

    public double GridCellWidth => GridThumbSize + 20;
    public double GridCellHeight => GridThumbSize * 1.32 + 34;

    /// <summary>La vista se suscribe para marcar páginas (índices tras el cambio) una vez reconstruida la lista.</summary>
    public event Action<IReadOnlyList<int>>? PagesSelectionRequested;

    private void SelectPagesAfterChange(IReadOnlyList<int> pages)
    {
        SelectedPageIndexes = pages;
        PagesSelectionRequested?.Invoke(pages);
    }

    private void RotateSelected(int degrees)
    {
        var pages = TargetPages;
        if (Edit(ed => ed.RotatePages(pages, degrees), structural: true)) SelectPagesAfterChange(pages);
    }

    /// <summary>
    /// Mueve las páginas indicadas junto a la página <paramref name="target"/> (arrastrar y soltar en la lista lateral):
    /// hacia arriba quedan antes de la página destino; hacia abajo, después.
    /// </summary>
    public void MovePages(IReadOnlyList<int> moving, int target)
    {
        if (moving.Count == 0 || moving.Contains(target)) return;
        MovePagesToGap(moving, target > moving.Min() ? target + 1 : target);
    }

    /// <summary>Mueve las páginas al hueco <paramref name="gap"/> (0 = antes de la primera, Count = después de la última).</summary>
    public void MovePagesToGap(IReadOnlyList<int> moving, int gap)
    {
        var order = OrderWithInsertion(moving.Order().ToList(), gap, remove: true);
        if (order.SequenceEqual(Enumerable.Range(0, Pages.Count))) return;
        if (Edit(ed => ed.ReorderPages(order), structural: true))
        {
            var newIndexes = moving.Select(m => order.IndexOf(m)).Order().ToList();
            GoToPage(newIndexes[0]);
            SelectPagesAfterChange(newIndexes);
        }
    }

    /// <summary>Inserta copias de las páginas en el hueco <paramref name="gap"/> (arrastrar con Ctrl).</summary>
    public void DuplicatePagesToGap(IReadOnlyList<int> source, int gap)
    {
        var copies = source.Order().ToList();
        var order = OrderWithInsertion(copies, gap, remove: false);
        if (Edit(ed => ed.ReorderPages(order), structural: true))
        {
            // Las copias ocupan justo el hueco; las originales siguen donde estaban.
            var newIndexes = Enumerable.Range(gap, copies.Count).ToList();
            GoToPage(gap);
            SelectPagesAfterChange(newIndexes);
            SearchStatus = copies.Count == 1 ? "Página duplicada" : $"{copies.Count} páginas duplicadas";
        }
    }

    /// <summary>
    /// Orden nuevo de páginas al insertar <paramref name="moving"/> en el hueco. Con <paramref name="remove"/> las páginas
    /// se mueven (se quitan de su sitio); si no, se copian y las originales se quedan.
    /// </summary>
    private List<int> OrderWithInsertion(List<int> moving, int gap, bool remove)
    {
        var set = moving.ToHashSet();
        var all = Enumerable.Range(0, Pages.Count).ToList();
        var before = all.Where(i => i < gap && (!remove || !set.Contains(i)));
        var after = all.Where(i => i >= gap && (!remove || !set.Contains(i)));
        return before.Concat(moving).Concat(after).ToList();
    }

    /// <summary>True si soltar las páginas en ese hueco dejaría el documento igual (no hace falta indicador ni operación).</summary>
    public bool IsNoOpMove(IReadOnlyList<int> moving, int gap) =>
        OrderWithInsertion(moving.Order().ToList(), gap, remove: true).SequenceEqual(Enumerable.Range(0, Pages.Count));

    /// <summary>Libera las miniaturas grandes al salir de la vista de organizar (ahorra memoria).</summary>
    public void ReleaseGridThumbnails()
    {
        foreach (var p in Pages) p.ReleaseGridThumbnail();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void DuplicatePages()
    {
        var pages = TargetPages;
        DuplicatePagesToGap(pages, pages[^1] + 1);
    }

    /// <summary>Inserta las páginas de los PDF indicados en el hueco (soltar archivos sobre la cuadrícula).</summary>
    public void InsertPdfFilesAtGap(IReadOnlyList<string> files, int gap)
    {
        int inserted = 0;
        if (Edit(ed =>
        {
            int position = gap;
            foreach (var file in files)
            {
                int count;
                try { count = ed.InsertPagesFrom(file, position); }
                catch (PdfPasswordRequiredException)
                {
                    var pwd = PasswordDialog.Ask(Path.GetFileName(file));
                    if (pwd is null) continue;
                    count = ed.InsertPagesFrom(file, position, pwd);
                }
                position += count;
                inserted += count;
            }
            if (inserted == 0) throw new OperationCanceledException();
        }, structural: true))
        {
            GoToPage(gap);
            SelectPagesAfterChange(Enumerable.Range(gap, inserted).ToList());
            SearchStatus = $"Insertadas {inserted} página(s)";
        }
    }
}
