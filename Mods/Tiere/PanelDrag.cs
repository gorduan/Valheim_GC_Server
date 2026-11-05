using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GC.Server.Leash;

/// <summary>
/// Ziehbares Feld (Gorduan, 04.11.2026, Idee aus EpicLoot 0.15.6, dessen Splitterfeld sich am Rahmen ziehen lässt): Ziehen am Rahmen
/// verschiebt, Doppelklick legt es an seinen Platz zurück. Ein Zug, der auf einem Platz oder Knopf beginnt, gehört dem Platz
/// (Items ziehen), nicht dem Feld.
/// </summary>
internal sealed class PanelDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
{
	/// <summary>Verschiebung in Einheiten des Felds.</summary>
	public Action<Vector2>? Moved;
	public Action? Done;
	public Action? Reset;
	public Func<bool>? Allowed;

	private bool dragging;

	public void OnBeginDrag(PointerEventData eventData)
	{
		GameObject? hit = eventData.pointerPressRaycast.gameObject;
		dragging = (Allowed == null || Allowed()) && (hit == null || (hit.GetComponentInParent<Selectable>() == null && hit.GetComponentInParent<InventoryElement>() == null));
	}

	public void OnDrag(PointerEventData eventData)
	{
		if (dragging)
		{
			Moved?.Invoke(eventData.delta / Mathf.Max(0.0001f, transform.lossyScale.x));
		}
	}

	public void OnEndDrag(PointerEventData eventData)
	{
		if (dragging)
		{
			dragging = false;
			Done?.Invoke();
		}
	}

	public void OnPointerClick(PointerEventData eventData)
	{
		GameObject? hit = eventData.pointerPressRaycast.gameObject;
		bool frame = hit == null || (hit.GetComponentInParent<Selectable>() == null && hit.GetComponentInParent<InventoryElement>() == null);
		if (eventData.clickCount >= 2 && frame && (Allowed == null || Allowed()))
		{
			Reset?.Invoke();
		}
	}

	private void OnDisable() => dragging = false;

	/// <summary>Durchsichtiger Rahmen um ein Feld (hinter seinen Plätzen), an dem man es greifen kann.</summary>
	internal static Image Frame(Transform parent, float margin)
	{
		GameObject go = new("GC_DragFrame", typeof(RectTransform));
		go.transform.SetParent(parent, false);
		go.transform.SetAsFirstSibling();
		RectTransform rect = (RectTransform)go.transform;
		rect.anchorMin = Vector2.zero;
		rect.anchorMax = Vector2.one;
		rect.offsetMin = new Vector2(-margin, -margin);
		rect.offsetMax = new Vector2(margin, margin);
		Image image = go.AddComponent<Image>();
		image.color = new Color(0f, 0f, 0f, 0f);
		image.raycastTarget = true;
		return image;
	}
}
