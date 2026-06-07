using UnityEngine;
using UnityEngine.UI;

public class StaminaBar : MonoBehaviour
{
    public GameObject staminaBar;
    public Image fillImage;
    public Color fullColour = Color.green;
    public Color exhaustedColour = Color.red;

    [Range(0f, 1f)]
    public float lowThreshold = 0.2f;

    private PlayerController _player;

    public void Bind(PlayerController player)
    {
        _player = player;
        Debug.Log($"[StaminaBar] Bound to {player.name}");
    }

    private void Update()
    {
        if (_player == null || fillImage == null) return;

        float t = _player.StaminaNetworked.Value / _player.stamina.maxStamina;
        fillImage.fillAmount = t;
        fillImage.color = Color.Lerp(exhaustedColour, fullColour,
                            Mathf.InverseLerp(0f, lowThreshold, t));
    }
}