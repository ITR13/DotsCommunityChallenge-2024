using System;
using System.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

public class VisualController : MonoBehaviour
{
    [SerializeField] private GameObject _quad, _graphy;
    [SerializeField] private Text _text;

    private EntityQuery _calcQuery, _statsQuery;
    private CalcMode _prevCalc;
    private Stats _prevStats;

    private IEnumerator Start()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        var entityManager = world.EntityManager;
        _calcQuery = entityManager.CreateEntityQuery(typeof(CalcMode));
        _statsQuery = entityManager.CreateEntityQuery(typeof(Stats));
        var visualizerQuery = entityManager.CreateEntityQuery(typeof(Visualizer));
        Debug.Log("Waiting for visualizer...");

        while (visualizerQuery.IsEmpty)
        {
            yield return null;
        }
        Application.targetFrameRate = -1;
        
        Debug.Log("Getting visualizer!");
        
        var visualizer = visualizerQuery.GetSingleton<Visualizer>();

        Debug.Log("Creating new material");
        var material = new Material(visualizer.Material);
        visualizer.Material = material;
        Debug.Log("Setting material to renderer");
        _quad.GetComponent<MeshRenderer>().sharedMaterial = visualizer.Material;
    }

    private void Update()
    {
        if (_calcQuery.IsEmpty) return;
        if (_calcQuery.IsEmpty || _statsQuery.IsEmpty) return;
        if (_calcQuery.IsEmpty) return;

        var calc = _calcQuery.GetSingleton<CalcMode>();
        var stats = _statsQuery.GetSingleton<Stats>();

        if (calc.Equals(_prevCalc) && _prevStats.ActiveGroups == stats.ActiveGroups && _prevStats.InactiveGroups == stats.InactiveGroups)
        {
            return;
        }

        _prevCalc = calc;
        _prevStats = stats;

        _quad.SetActive(calc.RenderSize > 0);
        _graphy.SetActive(calc.ShowUi);

        var algorithmName = Enum.GetName(typeof(Algorithm), calc.Algorithm);
        var pauseColor = calc.Paused ? "<color=red>" : "<color=green>";
        var renderColor = calc.RenderSize <= 0 ? "<color=red>" : "<color=yellow>";
        
        _text.text = @$"
<b>Algorithm:</b> {algorithmName}
<b>VisScale:</b> {renderColor}{calc.RenderSize}</color>
<b>Simulating:</b> {pauseColor}{!calc.Paused}</color>
<b>ActiveGroups:</b> {stats.ActiveGroups}
<b>InactiveGroups:</b> {stats.InactiveGroups}
".Trim();
    }
}