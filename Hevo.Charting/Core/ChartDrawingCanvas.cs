using Hevo.Charting.Abstractions;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hevo.Charting
{
    public class ChartDrawingCanvas : Canvas
    {
        /// <summary>
        /// 和所有visual的交互
        /// </summary>
        public ChartDrawingCanvas()
        {
            _visuals = new VisualCollection(this);
        }

        private VisualCollection _visuals;
        public void InsertVisual(int physicalIndex, ChartLayer visual)
        {
            if (physicalIndex >= _visuals.Count)
                _visuals.Add(visual);
            else
                _visuals.Insert(physicalIndex, visual);
        }

        internal void RemoveVisual(ChartLayer visual) => _visuals.Remove(visual);
        public DrawingVisual CreateVisual()
        {
            var v = new DrawingVisual();
            _visuals.Add(v);
            return v;
        }
        protected override int VisualChildrenCount => _visuals.Count;
        protected override Visual GetVisualChild(int index)
        {
            return _visuals[index];
        }

    }
}
