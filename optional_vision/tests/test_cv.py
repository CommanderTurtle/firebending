import cv2
import numpy as np
from openai_buffer_vision.cv import detect_regions
from openai_buffer_vision.model import Viewport


def test_detect_regions_finds_geometry_without_claiming_semantics() -> None:
    image = np.zeros((200, 300, 3), dtype=np.uint8)
    cv2.rectangle(image, (40, 50), (180, 110), (255, 255, 255), 2)

    elements = detect_regions(image, Viewport(width=300, height=200), min_area=100)

    assert elements
    assert all(element.role == "visual-region" for element in elements)
    assert all(element.source == "opencv" for element in elements)
